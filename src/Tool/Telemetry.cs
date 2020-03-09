﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#if TELEMETRY

using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Applications.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Jupyter.Core;
using Microsoft.Quantum.QsCompiler.CompilationBuilder;
using Microsoft.Quantum.Simulation.Simulators;
using static Microsoft.Jupyter.Core.BaseEngine;
using Microsoft.Quantum.IQSharp.Jupyter;

namespace Microsoft.Quantum.IQSharp
{
    public class TelemetryService : ITelemetryService
    {
        private const string TOKEN = "55aee962ee9445f3a86af864fc0fa766-48882422-3439-40de-8030-228042bd9089-7794";

        public TelemetryService(
            ILogger<TelemetryService> logger,
            IEventService eventService)
        {
            var config = Program.Configuration;
            Logger = logger;
            Logger.LogInformation("Starting Telemetry.");
            Logger.LogDebug($"DeviceId: {GetDeviceId()}.");

            InitLogManager(config);

            eventService.OnKernelStarted().On += (kernelApp) =>
            {
                TelemetryLogger.LogEvent("SessionStart".AsTelemetryEvent());
            };
            eventService.OnKernelStopped().On += (kernelApp) =>
            {
                TelemetryLogger.LogEvent("SessionEnd".AsTelemetryEvent());
                LogManager.Teardown();
            };

            eventService.OnServiceInitialized<IMetadataController>().On += (metadataController) =>
                metadataController.MetadataChanged += (metadataController, propertyChanged) =>
                    SetSharedContextIfChanged(metadataController, propertyChanged,
                                                nameof(metadataController.ClientId),
                                                nameof(metadataController.UserAgent),
                                                nameof(metadataController.ClientCountry),
                                                nameof(metadataController.ClientLanguage),
                                                nameof(metadataController.ClientHost),
                                                nameof(metadataController.ClientIsNew));
            eventService.OnServiceInitialized<ISnippets>().On += (snippets) =>
                snippets.SnippetCompiled += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
            eventService.OnServiceInitialized<IWorkspace>().On += (workspace) =>
                workspace.Reloaded += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
            eventService.OnServiceInitialized<IReferences>().On += (references) =>
                references.PackageLoaded += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
            eventService.OnServiceInitialized<IExecutionEngine>().On += (executionEngine) =>
            {
                if (executionEngine is BaseEngine engine)
                {
                    engine.MagicExecuted += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
                    engine.HelpExecuted += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
                }
            };
        }

        public Applications.Events.ILogger TelemetryLogger { get; private set; }
        public ILogger<TelemetryService> Logger { get; }

        public void InitLogManager(IConfiguration config)
        {
            LogManager.Start(new LogConfiguration());
            TelemetryLogger = LogManager.GetLogger(TOKEN, out _);
            LogManager.SetSharedContext("AppInfo.Id", "iq#");
            LogManager.SetSharedContext("AppInfo.Version", Jupyter.Constants.IQSharpKernelProperties.KernelVersion);
            LogManager.SetSharedContext("CompilerVersion".WithTelemetryNamespace(), typeof(CompilationUnitManager).Assembly.GetName().Version.ToString());
            LogManager.SetSharedContext("SimulationVersion".WithTelemetryNamespace(), typeof(QuantumSimulator).Assembly.GetName().Version.ToString());
            LogManager.SetSharedContext("Root".WithTelemetryNamespace(), Path.GetFileName(Directory.GetCurrentDirectory()), PiiKind.GenericData);
            LogManager.SetSharedContext("DeviceId".WithTelemetryNamespace(), GetDeviceId(), PiiKind.GenericData);
            LogManager.SetSharedContext("UserAgent".WithTelemetryNamespace(), config?.GetValue<string>("UserAgent"));
            LogManager.SetSharedContext("HostingEnvironment".WithTelemetryNamespace(), config?.GetValue<string>("HostingEnvironment"));
        }

        /// <summary>
        /// Return an Id for this device, namely, the first non-empty MAC address it can find across all network interfaces (if any).
        /// </summary>
        public static string GetDeviceId() =>
            NetworkInterface.GetAllNetworkInterfaces()?
                .Select(n => n?.GetPhysicalAddress()?.ToString())
                .Where(address => address != null && !string.IsNullOrWhiteSpace(address) && !address.StartsWith("000000"))
                .FirstOrDefault();

        public void SetSharedContextIfChanged(IMetadataController metadataController, string propertyChanged, params string[] propertyAllowlist)
        {
            if (propertyAllowlist == null
                || !propertyAllowlist.Contains(propertyChanged)) return;
            var property = typeof(IMetadataController)
                            .GetProperties()
                            .Where(p => p.Name == propertyChanged && p.CanRead)
                            .FirstOrDefault();
            if (property != null)
            {
                var value = $"{property.GetValue(metadataController)}";
                Logger.LogInformation($"ClientMetadataChanged: {property.Name}={value}");
                LogManager.SetSharedContext(property.Name, value);
                TelemetryLogger.LogEvent($"ClientMetadataChanged".AsTelemetryEvent());
            }
        }
    }

    public static class TelemetryExtensions
    {
        public static string WithTelemetryNamespace(this string name) =>
            $"Quantum.IQSharp.{name}";

        public static EventProperties AsTelemetryEvent(this string name) =>
            new EventProperties() { Name = name.WithTelemetryNamespace() };

        public static EventProperties AsTelemetryEvent(this ReloadedEventArgs info)
        {
            var evt = new EventProperties() { Name = "WorkspaceReload".WithTelemetryNamespace() };

            evt.SetProperty("Workspace".WithTelemetryNamespace(), Path.GetFileName(info.Workspace), PiiKind.GenericData);
            evt.SetProperty("Status".WithTelemetryNamespace(), info.Status);
            evt.SetProperty("FileCount".WithTelemetryNamespace(), info.FileCount);
            evt.SetProperty("Errors".WithTelemetryNamespace(), string.Join(",", info.Errors?.OrderBy(e => e) ?? Enumerable.Empty<string>()));
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString());

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this SnippetCompiledEventArgs info)
        {
            var evt = new EventProperties() { Name = "Compile".WithTelemetryNamespace() };

            evt.SetProperty("Status".WithTelemetryNamespace(), info.Status);
            evt.SetProperty("Errors".WithTelemetryNamespace(), string.Join(",", info.Errors?.OrderBy(e => e) ?? Enumerable.Empty<string>()));
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString());

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this PackageLoadedEventArgs info)
        {
            var evt = new EventProperties() { Name = "PackageLoad".WithTelemetryNamespace() };

            evt.SetProperty("PackageId".WithTelemetryNamespace(), info.PackageId);
            evt.SetProperty("PackageVersion".WithTelemetryNamespace(), info.PackageVersion);
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString());

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this ExecutedEventArgs info)
        {
            var evt = new EventProperties() { Name = "Action".WithTelemetryNamespace() };

            evt.SetProperty("Command".WithTelemetryNamespace(), info.Symbol?.Name);
            evt.SetProperty("Kind".WithTelemetryNamespace(), info.Symbol?.Kind.ToString());
            evt.SetProperty("Status".WithTelemetryNamespace(), info.Result.Status.ToString());
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString());

            return evt;
        }
    }


}

#endif
