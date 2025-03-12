// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Represents a context for publishing Docker Compose configurations for a distributed application.
/// </summary>
/// <remarks>
/// This context facilitates the generation of Docker Compose files using the provided application model,
/// publisher options, and execution context. It handles the allocation of ports for services and ensures
/// that the Docker Compose configuration file is created in the specified output path.
/// </remarks>
internal sealed class DockerComposePublishingContext(
    DistributedApplicationExecutionContext executionContext,
    DockerComposePublisherOptions publisherOptions,
    ILogger logger,
    CancellationToken cancellationToken = default)
{
    public readonly PortAllocator PortAllocator = new();
    private readonly Dictionary<IResource, ComposeServiceContext> _composeServices = [];

    private ILogger Logger => logger;

    internal async Task WriteModel(DistributedApplicationModel model)
    {
        if (executionContext.IsRunMode)
        {
            return;
        }

        logger.StartGeneratingDockerCompose();

        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(publisherOptions.OutputPath);

        if (model.Resources.Count == 0)
        {
            logger.EmptyModel();
            return;
        }

        var outputFile = await WriteDockerComposeOutput(model).ConfigureAwait(false);

        logger.FinishGeneratingDockerCompose(outputFile);
    }

    private async Task<string> WriteDockerComposeOutput(DistributedApplicationModel model)
    {
        var defaultNetwork = new Network
        {
            Name = publisherOptions.ExistingNetworkName ?? "aspire",
            Driver = "bridge",
        };

        var composeFile = new ComposeFile();
        composeFile.AddNetwork(defaultNetwork);

        foreach (var resource in model.Resources)
        {
            if (resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var lastAnnotation) &&
                lastAnnotation == ManifestPublishingCallbackAnnotation.Ignore)
            {
                continue;
            }

            if (!resource.IsContainer() && resource is not ProjectResource)
            {
                continue;
            }

            var composeServiceContext = await ProcessResourceAsync(resource).ConfigureAwait(false);

            var composeService = composeServiceContext.BuildComposeService();

            HandleComposeFileVolumes(composeServiceContext, composeFile);

            composeService.Networks =
            [
                defaultNetwork.Name,
            ];

            composeFile.AddService(composeService);
        }

        var composeOutput = composeFile.ToYaml();
        var outputFile = Path.Combine(publisherOptions.OutputPath!, "docker-compose.yaml");
        Directory.CreateDirectory(publisherOptions.OutputPath!);
        await File.WriteAllTextAsync(outputFile, composeOutput, cancellationToken).ConfigureAwait(false);
        return outputFile;
    }

    private async Task<ComposeServiceContext> ProcessResourceAsync(IResource resource)
    {
        if (!_composeServices.TryGetValue(resource, out var context))
        {
            _composeServices[resource] = context = new(resource, this);
            await context.ProcessResourceAsync(executionContext, cancellationToken).ConfigureAwait(false);
        }

        return context;
    }

    private static void HandleComposeFileVolumes(ComposeServiceContext composeServiceContext, ComposeFile composeFile)
    {
        foreach (var volume in composeServiceContext.Volumes.Where(volume => volume.Type != "bind"))
        {
            if (composeFile.Volumes.ContainsKey(volume.Name))
            {
                continue;
            }

            var newVolume = new Volume
            {
                Name = volume.Name,
                Driver = volume.Driver ?? "local",
                External = volume.External,
            };

            composeFile.AddVolume(newVolume);
        }
    }

    private sealed class ComposeServiceContext(IResource resource, DockerComposePublishingContext composePublishingContext)
    {
        private record struct EndpointMapping(string Scheme, string Host, int InternalPort, int ExposedPort, bool IsHttpIngress, bool External);

        private readonly Dictionary<object, string> _resolvedParameters = [];
        private readonly Dictionary<string, string> _rawParameterValues = [];
        private readonly Dictionary<string, EndpointMapping> _endpointMapping = [];
        public Dictionary<string, string> EnvironmentVariables { get; } = [];
        public List<string> Commands { get; } = [];
        public Dictionary<string, object> Parameters { get; } = [];

        public List<Volume> Volumes { get; } = [];

        public Service BuildComposeService()
        {
            if (!TryGetContainerImageName(resource, out var containerImageName))
            {
                composePublishingContext.Logger.FailedToGetContainerImage(resource.Name);
            }

            var composeService = new Service
            {
                Name = resource.Name.ToLowerInvariant(),
            };

            ApplyParametersForContext();

            SetEntryPoint(composeService);
            AddEnvironmentVariablesAndCommandLineArgs(composeService);
            AddPorts(composeService);
            AddVolumes(composeService);
            SetContainerImage(containerImageName, composeService);

            return composeService;
        }

        private void AddVolumes(Service composeService)
        {
            if (Volumes.Count == 0)
            {
                return;
            }

            foreach (var volume in Volumes)
            {
                composeService.AddVolume(volume);
            }
        }

        private void AddPorts(Service composeService)
        {
            if (_endpointMapping.Count == 0)
            {
                return;
            }

            foreach (var (_, mapping) in _endpointMapping)
            {
                var internalPort = mapping.InternalPort.ToString(CultureInfo.InvariantCulture);
                var exposedPort = mapping.ExposedPort.ToString(CultureInfo.InvariantCulture);

                composeService.Ports.Add($"{exposedPort}:{internalPort}");
            }
        }

        private static void SetContainerImage(string? containerImageName, Service composeService)
        {
            if (containerImageName is not null)
            {
                composeService.Image = containerImageName;
            }
        }

        private static bool TryGetContainerImageName(IResource resource, out string? containerImageName)
        {
            // If the resource has a Dockerfile build annotation, we don't have the image name
            // it will come as a parameter
            if (resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _))
            {
                containerImageName = null;
                return false;
            }

            return resource.TryGetContainerImageName(out containerImageName);
        }

        public async Task ProcessResourceAsync(DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            ProcessEndpoints();
            ProcessVolumes();

            await ProcessEnvironmentAsync(executionContext, cancellationToken).ConfigureAwait(false);
            await ProcessArgumentsAsync(cancellationToken).ConfigureAwait(false);
        }

        private void ProcessEndpoints()
        {
            if (!resource.TryGetEndpoints(out var endpoints))
            {
                return;
            }

            foreach (var endpoint in endpoints)
            {
                var internalPort = endpoint.TargetPort ?? composePublishingContext.PortAllocator.AllocatePort();
                composePublishingContext.PortAllocator.AddUsedPort(internalPort);

                var exposedPort = composePublishingContext.PortAllocator.AllocatePort();
                composePublishingContext.PortAllocator.AddUsedPort(exposedPort);

                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, resource.Name, internalPort, exposedPort, false, endpoint.IsExternal);
            }
        }

        private async Task ProcessArgumentsAsync(CancellationToken cancellationToken)
        {
            if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var commandLineArgsCallbackAnnotations))
            {
                var context = new CommandLineArgsCallbackContext([], cancellationToken: cancellationToken);

                foreach (var c in commandLineArgsCallbackAnnotations)
                {
                    await c.Callback(context).ConfigureAwait(false);
                }

                foreach (var arg in context.Args)
                {
                    var value = await ProcessValueAsync(arg).ConfigureAwait(false);

                    if (value is not string str)
                    {
                        throw new NotSupportedException("Command line args must be strings");
                    }

                    Commands.Add(new(str));
                }
            }
        }

        private async Task ProcessEnvironmentAsync(DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
            {
                var context = new EnvironmentCallbackContext(executionContext, cancellationToken: cancellationToken);

                foreach (var c in environmentCallbacks)
                {
                    await c.Callback(context).ConfigureAwait(false);
                }

                foreach (var kv in context.EnvironmentVariables)
                {
                    var value = await ProcessValueAsync(kv.Value).ConfigureAwait(false);

                    EnvironmentVariables[kv.Key] = value.ToString() ?? string.Empty;
                }
            }
        }
        private void ProcessVolumes()
        {
            if (!resource.TryGetContainerMounts(out var mounts))
            {
                return;
            }

            foreach (var volume in mounts)
            {
                if (volume.Source is null || volume.Target is null)
                {
                    throw new InvalidOperationException("Volume source and target must be set");
                }

                var composeVolume = new Volume
                {
                    Name = volume.Source,
                    Type = volume.Type == ContainerMountType.BindMount ? "bind" : "volume",
                    Target = volume.Target,
                    Source = volume.Source,
                    ReadOnly = volume.IsReadOnly,
                };

                Volumes.Add(composeVolume);
            }
        }

        private static string GetValue(EndpointMapping mapping, EndpointProperty property)
        {
            var (scheme, host, internalPort, exposedPort, isHttpIngress, _) = mapping;

            return property switch
            {
                EndpointProperty.Url => GetHostValue($"{scheme}://", suffix: isHttpIngress ? null : $":{internalPort}"),
                EndpointProperty.Host or EndpointProperty.IPV4Host => GetHostValue(),
                EndpointProperty.Port => internalPort.ToString(CultureInfo.InvariantCulture),
                EndpointProperty.HostAndPort => GetHostValue(suffix: $":{internalPort}"),
                EndpointProperty.TargetPort => $"{exposedPort}",
                EndpointProperty.Scheme => scheme,
                _ => throw new NotSupportedException(),
            };

            string GetHostValue(string? prefix = null, string? suffix = null)
            {
                return $"{prefix}{host}{suffix}";
            }
        }

        private async Task<object> ProcessValueAsync(object value)
        {
            while (true)
            {
                if (value is string s)
                {
                    return s;
                }

                if (value is EndpointReference ep)
                {
                    var context = ep.Resource == resource
                        ? this
                        : await composePublishingContext.ProcessResourceAsync(ep.Resource)
                            .ConfigureAwait(false);

                    var mapping = context._endpointMapping[ep.EndpointName];

                    var url = GetValue(mapping, EndpointProperty.Url);

                    return url;
                }

                if (value is ParameterResource param)
                {
                    return await AllocateParameter(param).ConfigureAwait(false) ?? throw new InvalidOperationException("Parameter name is null");
                }

                if (value is ConnectionStringReference cs)
                {
                    value = cs.Resource.ConnectionStringExpression;
                    continue;
                }

                if (value is IResourceWithConnectionString csrs)
                {
                    value = csrs.ConnectionStringExpression;
                    continue;
                }

                if (value is EndpointReferenceExpression epExpr)
                {
                    var context = epExpr.Endpoint.Resource == resource
                        ? this
                        : await composePublishingContext.ProcessResourceAsync(epExpr.Endpoint.Resource).ConfigureAwait(false);

                    var mapping = context._endpointMapping[epExpr.Endpoint.EndpointName];

                    var val = GetValue(mapping, epExpr.Property);

                    return val;
                }

                if (value is ReferenceExpression expr)
                {
                    if (expr is {Format: "{0}", ValueProviders.Count: 1})
                    {
                        return (await ProcessValueAsync(expr.ValueProviders[0]).ConfigureAwait(false)).ToString() ?? string.Empty;
                    }

                    var args = new object[expr.ValueProviders.Count];
                    var index = 0;

                    foreach (var vp in expr.ValueProviders)
                    {
                        var val = await ProcessValueAsync(vp).ConfigureAwait(false);
                        args[index++] = val ?? throw new InvalidOperationException("Value is null");
                    }

                    return string.Format(CultureInfo.InvariantCulture, expr.Format, args);
                }

                // todo: ideally we should have processed all resources that we can before getting here...
                // This is probably going to include removing the resource from the model if its not processable during publishing in Docker - BicepResources?
                // The problem there is that we'd need to take reference on Azure hosting for that.
                // Approach: Maybe we filter the incoming resources and remove the ones that are not processable?
                if (value is IManifestExpressionProvider r)
                {
                    composePublishingContext.Logger.NotSupportedResourceWarning(nameof(value), r.GetType().Name);
                }

                return value; // todo: we need to never get here really...
            }
        }

        private static Task<string> ResolveParameterValue(IManifestExpressionProvider parameter)
        {
            // Placeholder for resolving the actual parameter value
            // Where does it come from? How do we resolve it?
            // From user input?
            // From State?
            // To Discuss
            // Will work for AppSettings values and generated values as it stands.

            if (parameter is not ParameterResource res)
            {
                throw new InvalidOperationException("Parameter is not a ParameterResource");
            }

            if (res.Secret) 
            {
                // Treat secrets as environment variable placeholders as for now
                // this doesn't handle generation of parameter values with defaults
                var env = res.Name.ToUpperInvariant().Replace("-", "_");
                return Task.FromResult($"${{{env}}}");
            }

            return Task.FromResult(res.Value);
        }

        private async Task<string> AllocateParameter(IManifestExpressionProvider parameter)
        {
            if (!_resolvedParameters.TryGetValue(parameter, out var parameterName))
            {
                parameterName = parameter.ValueExpression
                    .Replace("{", "")
                    .Replace("}", "")
                    .Replace(".", "_")
                    .Replace("-", "_")
                    .ToLowerInvariant();

                _resolvedParameters[parameter] = parameterName;
            }

            if (!_rawParameterValues.ContainsKey(parameterName))
            {
                var actualValue = await ResolveParameterValue(parameter).ConfigureAwait(false);
                _rawParameterValues[parameterName] = actualValue;
            }

            Parameters[parameterName] = parameter;

            return parameterName;
        }

        private void SetEntryPoint(Service composeService)
        {
            if (resource is ContainerResource {Entrypoint: { } entrypoint})
            {
                composeService.Entrypoint.Add(entrypoint);
            }
        }

        private void AddEnvironmentVariablesAndCommandLineArgs(Service composeService)
        {
            if (EnvironmentVariables.Count > 0)
            {
                foreach (var variable in EnvironmentVariables)
                {
                    composeService.AddEnvironmentalVariable(variable.Key, variable.Value);
                }
            }

            if (Commands.Count > 0)
            {
                composeService.Command.AddRange(Commands);
            }
        }

        private void ApplyParametersForContext()
        {
            if (Parameters.Count == 0)
            {
                return;
            }

            foreach (var parameter in _resolvedParameters)
            {
                var parameterName = parameter.Value;

                foreach (var envVar in EnvironmentVariables.Where(x => x.Value.Contains(parameterName)))
                {
                    var envVarValue = _rawParameterValues.TryGetValue(parameterName, out var value)
                        ? value
                        : string.Empty;

                    EnvironmentVariables[envVar.Key] = envVar.Value.Replace(parameterName, envVarValue);
                }
            }
        }
    }
}
