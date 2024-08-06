using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using GitHub.DistributedTask.WebApi;
using GitHub.DistributedTask.Pipelines;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;

namespace GitHub.Runner.Worker
{
    [ServiceLocator(Default = typeof(OSWarningChecker))]
    public interface IOSWarningChecker : IRunnerService
    {
        Task CheckOSAsync(IExecutionContext context, IList<OSWarning> osWarnings);
    }

    public sealed class OSWarningChecker : RunnerService, IOSWarningChecker
    {
        private static readonly TimeSpan s_matchTimeout = TimeSpan.FromMilliseconds(100);
        private static readonly RegexOptions s_regexOptions = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

        public async Task CheckOSAsync(IExecutionContext context, IList<OSWarning> osWarnings)
        {
            ArgUtil.NotNull(context, nameof(context));
            ArgUtil.NotNull(osWarnings, nameof(osWarnings));
            context.Output("Testing runner upgrade compatibility");
            var isCompatible = true;
#if !OS_WINDOWS && !OS_OSX
            isCompatible = await TestMatchFilesAsync(context, osWarnings);
#endif
            await CheckDotNet8CompatibilityAsync(context, omitAnnotation: !isCompatible);
        }

#if !OS_WINDOWS && !OS_OSX
        public async Task<bool> TestMatchFilesAsync(IExecutionContext context, IList<OSWarning> osWarnings)
        {
            foreach (var osWarning in osWarnings)
            {
                if (string.IsNullOrEmpty(osWarning.FilePath))
                {
                    Trace.Error("The file path is not specified in the OS warning check.");
                    continue;
                }

                if (string.IsNullOrEmpty(osWarning.RegularExpression))
                {
                    Trace.Error("The regular expression is not specified in the OS warning check.");
                    continue;
                }

                if (string.IsNullOrEmpty(osWarning.Warning))
                {
                    Trace.Error("The warning message is not specified in the OS warning check.");
                    continue;
                }

                try
                {
                    if (File.Exists(osWarning.FilePath))
                    {
                        var lines = await File.ReadAllLinesAsync(osWarning.FilePath, context.CancellationToken);
                        var regex = new Regex(osWarning.RegularExpression, s_regexOptions, s_matchTimeout);
                        foreach (var line in lines)
                        {
                            if (regex.IsMatch(line))
                            {
                                context.Warning(osWarning.Warning);
                                context.Global.JobTelemetry.Add(new JobTelemetry() { Type = JobTelemetryType.General, Message = $"OS warning: {osWarning.Warning}" });
                                return false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.Error("An error occurred while checking OS warnings for file '{0}' and regex '{1}'.", osWarning.FilePath, osWarning.RegularExpression);
                    Trace.Error(ex);
                    context.Global.JobTelemetry.Add(new JobTelemetry() { Type = JobTelemetryType.General, Message = $"An error occurred while checking OS warnings for file '{osWarning.FilePath}' and regex '{osWarning.RegularExpression}': {ex.Message}" });
                }
            }

            return true;
        }
#endif

        private async Task CheckDotNet8CompatibilityAsync(IExecutionContext context, bool omitAnnotation)
        {
            try
            {
                List<string> output = new();
                object outputLock = new();
                using (var process = HostContext.CreateService<IProcessInvoker>())
                {
                    process.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs stdout)
                    {
                        if (!string.IsNullOrEmpty(stdout.Data))
                        {
                            lock (outputLock)
                            {
                                output.Add(stdout.Data);
                                Trace.Info(stdout.Data);
                            }
                        }
                    };

                    process.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs stderr)
                    {
                        if (!string.IsNullOrEmpty(stderr.Data))
                        {
                            lock (outputLock)
                            {
                                output.Add(stderr.Data);
                                Trace.Error(stderr.Data);
                            }
                        }
                    };

                    int exitCode = await process.ExecuteAsync(
                        workingDirectory: HostContext.GetDirectory(WellKnownDirectory.Root),
                        fileName: Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), "testDotNet8Compatibility", $"TestDotNet8Compatibility{IOUtil.ExeExtension}"),
                        arguments: string.Empty,
                        environment: null,
                        cancellationToken: context.CancellationToken);

                    var outputStr = string.Join("\n", output).Trim();
                    if (exitCode != 0 || !string.Equals(outputStr, "Hello from .NET 8!", StringComparison.Ordinal))
                    {
                        if (!omitAnnotation)
                        {
                            context.Warning("The runner is not compatible with .NET 8.");
                        }

                        var shortOutput = outputStr.Length > 200 ? string.Concat(outputStr.Substring(0, 200), "[...]") : outputStr;
                        context.Global.JobTelemetry.Add(new JobTelemetry() { Type = JobTelemetryType.General, Message = $".NET 8 OS compatibility test failed with exit code '{exitCode}' and output: {shortOutput}" });
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.Error("An error occurred while testing .NET 8 compatibility'");
                Trace.Error(ex);
                context.Global.JobTelemetry.Add(new JobTelemetry() { Type = JobTelemetryType.General, Message = $"An error occurred while testing .NET 8 compatibility; exception type '{ex.GetType().FullName}'; message: {ex.Message}" });
            }
        }
    }
}

