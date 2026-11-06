using System;
using System.IO;
using System.Threading.Tasks;

namespace Fraylo.Desktop.SmokeChecks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "project-first", StringComparison.OrdinalIgnoreCase))
        {
            return await ProjectFirstSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "analytics", StringComparison.OrdinalIgnoreCase))
        {
            return await AnalyticsSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "c1c3", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(args[0], "history", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(args[0], "t17backend", StringComparison.OrdinalIgnoreCase)))
        {
            return await JobRevisionAndHistorySmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "q1", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "capacity", StringComparison.OrdinalIgnoreCase)))
        {
            return await ProviderAccountAndCapacitySmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "t15", StringComparison.OrdinalIgnoreCase))
        {
            return await JobQueueSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "prompt", StringComparison.OrdinalIgnoreCase))
        {
            return await PromptEngineSmokeTests.RunAllTestsAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "content", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "ai-web", StringComparison.OrdinalIgnoreCase)))
        {
            AiWebContentCreationSmokeTests.RunTests();
            return 0;
        }

        if (args.Length > 0 && (string.Equals(args[0], "remediation", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "pvrepro", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "pv-repro", StringComparison.OrdinalIgnoreCase)))
        {
            return await PvRemediationRegressionTests.RunAllAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "workflow", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "video-workflow", StringComparison.OrdinalIgnoreCase)))
        {
            return await VideoWorkflowSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "provider", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "workflow-def", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "t18a", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "t18", StringComparison.OrdinalIgnoreCase)))
        {
            return await ProviderRegistryAndWorkflowSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "concurrency", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "browser-lifecycle", StringComparison.OrdinalIgnoreCase)))
        {
            return await AccountConcurrencyAndBrowserLifecycleSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "muse", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "museai", StringComparison.OrdinalIgnoreCase)))
        {
            return await MuseAiSessionProbeSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && (string.Equals(args[0], "chatgpt", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "openai", StringComparison.OrdinalIgnoreCase)))
        {
            return await ChatGptSessionProbeSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "tiktok", StringComparison.OrdinalIgnoreCase))
        {
            return await TikTokSessionProbeSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "facebook", StringComparison.OrdinalIgnoreCase))
        {
            return await FacebookSessionProbeSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "benchmark", StringComparison.OrdinalIgnoreCase))
        {
            int sampleCount = 7;
            if (args.Length > 1 && int.TryParse(args[1], out int parsedSamples) && parsedSamples > 0)
            {
                sampleCount = parsedSamples;
            }
            return await BrowserLaunchBenchmark.RunAllBenchmarksAsync(sampleCount);
        }

        if (args.Length > 0 && string.Equals(args[0], "browser", StringComparison.OrdinalIgnoreCase))
        {
            await BrowserBridgeSmokeTests.RunAllAsync();
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "t13", StringComparison.OrdinalIgnoreCase))
        {
            await BrowserSmokeTests.RunAllAsync();
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "fault5", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Executing Task T16 Fault 5 specifically...");
            await JobRecoverySmokeTests.Test_Fault5_StorageErrorAfterAiGeneration_RetriesFinalizeWithoutReGenerating();
            Console.WriteLine("  ✓ Fault 5 PASSED 100%");
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "fault", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Executing Task T16 Fault Injection Smoke Suite...");
            await JobRecoverySmokeTests.RunAllAsync();
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "t16", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Executing All Task T16 Smoke Suites...");
            await JobRecoverySmokeTests.RunAllAsync();
            await JobRetryAndCancelSmokeTests.RunAllAsync();
            await JobCrashRecoverySmokeTests.RunAllAsync();
            await BrowserSessionCheckpointSmokeTests.RunAllAsync();
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "retry", StringComparison.OrdinalIgnoreCase))
        {
            return await JobRetryAndCancelSmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "recovery", StringComparison.OrdinalIgnoreCase))
        {
            return await JobCrashRecoverySmokeTests.RunAllAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            await BrowserSessionCheckpointSmokeTests.RunAllAsync();
            return 0;
        }

        Console.WriteLine("=================================================================");
        Console.WriteLine("        Fraylo — WPF WebView2 Bridge Smoke Checks (T05)          ");
        Console.WriteLine("=================================================================");

        int passed = 0;
        int failed = 0;

        async Task RunCheckAsync(string testName, Func<Task> testAction)
        {
            Console.Write($"[RUN] {testName} ... ");
            try
            {
                await testAction();
                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL");
                Console.ResetColor();
                Console.WriteLine($"      -> Lỗi: {ex.GetType().Name}: {ex.Message}");
            }
        }

        void RunSyncCheck(string testName, Action testAction)
        {
            Console.Write($"[RUN] {testName} ... ");
            try
            {
                testAction();
                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL");
                Console.ResetColor();
                Console.WriteLine($"      -> Lỗi: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("\n[1] Bridge Message Router & Envelope Smoke Checks:");
        await RunCheckAsync(
            "Test 1: JSON envelope parser and dispatcher smoke check",
            BridgeSmokeTests.Test_JsonEnvelopeParserAndDispatcher_Success);

        await RunCheckAsync(
            "Test 2: Cancel message version mismatch does not cancel in-flight",
            BridgeSmokeTests.Test_CancelMessage_VersionMismatch_DoesNotCancelInFlight);

        await RunCheckAsync(
            "Test 3: Duplicate request ID rejected with CONCURRENCY_CONFLICT",
            BridgeSmokeTests.Test_DuplicateRequestId_RejectedWithConcurrencyConflict);

        await RunCheckAsync(
            "Test 4: Prohibited fields in nested arrays and allowed paths",
            BridgeSmokeTests.Test_ProhibitedFields_NestedArraysAndAllowedFields);

        await RunCheckAsync(
            "Test 8: Internal exception does not leak raw exception message or paths",
            BridgeSmokeTests.Test_BridgeMessageRouter_InternalException_DoesNotLeakRawMessageOrPaths);

        await RunCheckAsync(
            "Test 12: Router dispose cancels in-flight request within bounded timeout",
            BridgeSmokeTests.Test_Router_Dispose_CancelsInFlightRequest_Bounded);

        await RunCheckAsync(
            "Test 13: Router calls after dispose return null and do not throw",
            BridgeSmokeTests.Test_Router_CallsAfterDispose_ReturnNull);

        await RunCheckAsync(
            "Test 14: Concurrent router start and dispose does not throw ObjectDisposedException",
            BridgeSmokeTests.Test_Router_ConcurrentStartAndDispose_NoObjectDisposedException);

        await RunCheckAsync(
            "Test 15: Valid cancel message cancels in-flight request and cleans up registration",
            BridgeSmokeTests.Test_CancelMessage_ValidCancel_CancelsInFlightRequest_AndCleansUp);

        await RunCheckAsync(
            "Test 16: Untrusted origin rejected immediately and returns null",
            BridgeSmokeTests.Test_Router_UntrustedOrigin_RejectedAndReturnsNull);

        await RunCheckAsync(
            "Test 17: In-flight registration cleaned up on exception allowing subsequent request with same ID",
            BridgeSmokeTests.Test_Router_InFlightCleanup_OnException_AllowsSubsequentRequestWithSameId);

        Console.WriteLine("\n[2] Host Theme Service Smoke Checks:");
        RunSyncCheck(
            "Test 5: System theme default and fallback resolution",
            BridgeSmokeTests.Test_SystemTheme_DefaultAndFallback);

        Console.WriteLine("\n[3] Bridge Dispatcher Smoke Checks:");
        await RunCheckAsync(
            "Test 6: Allowed command without handler returns neutral message without task IDs",
            BridgeSmokeTests.Test_BridgeDispatcher_AllowedCommandWithoutHandler_ReturnsNeutralMessageWithoutTaskIds);

        await RunCheckAsync(
            "Test 7: Unknown command rejected with UNKNOWN_COMMAND",
            BridgeSmokeTests.Test_BridgeDispatcher_UnknownCommand_Rejected);

        Console.WriteLine("\n[4] Desktop Runtime Paths & Isolation Smoke Checks:");
        RunSyncCheck(
            "Test 18: Default paths resolve to production LocalAppData and remain unopened",
            DesktopRuntimePathsSmokeTests.Test_DefaultPaths_UsesLocalAppDataAndProductionDefaults);

        RunSyncCheck(
            "Test 19: Valid test pair inside temp normalized and accepted in both CLI formats",
            DesktopRuntimePathsSmokeTests.Test_ValidTestPair_NormalizedAndAccepted);

        RunSyncCheck(
            "Test 20: Missing one test parameter in pair rejected with ArgumentException",
            DesktopRuntimePathsSmokeTests.Test_MissingPair_RejectedWithArgumentException);

        RunSyncCheck(
            "Test 21: Relative paths rejected with ArgumentException",
            DesktopRuntimePathsSmokeTests.Test_RelativePath_RejectedWithArgumentException);

        RunSyncCheck(
            "Test 22: Paths outside temp directory rejected with ArgumentException",
            DesktopRuntimePathsSmokeTests.Test_OutsideTemp_RejectedWithArgumentException);

        RunSyncCheck(
            "Test 23: Production default paths rejected with ArgumentException",
            DesktopRuntimePathsSmokeTests.Test_ProductionCollision_RejectedWithArgumentException);

        RunSyncCheck(
            "Test 24: Identical database and profile paths rejected with ArgumentException",
            DesktopRuntimePathsSmokeTests.Test_IdenticalPaths_RejectedWithArgumentException);

        Console.WriteLine("\n[6] Task T10 Prompt Engine Smoke Checks:");
        await RunCheckAsync(
            "Task T10: Prompt Templates, Immutable Versions, Overrides, Preview, Rollback & Snapshot",
            async () =>
            {
                int exitCode = await PromptEngineSmokeTests.RunAllTestsAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"PromptEngineSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[7] Task T13 Browser Ownership & Registry Smoke Checks:");
        await RunCheckAsync(
            "Task T13: Browser Profile Registry, Single Owner Lock, Tab Pool, Login Queue & Quiescing",
            async () => await BrowserSmokeTests.RunAllAsync());

        Console.WriteLine("\n[8] Task R1 Desktop Browser Bridge Handlers Smoke Checks:");
        await RunCheckAsync(
            "Task R1: Browser Handlers (list_profiles, request_login, cancel_login, check_session, errors)",
            async () => await BrowserBridgeSmokeTests.RunAllAsync());

        Console.WriteLine("\n[10] Task T15 Job Queue & Scheduler Smoke Checks:");
        await RunCheckAsync(
            "Task T15: Durable Job Queue, Atomic Reservation, Fair Scheduler & Crash Recovery",
            async () =>
            {
                int exitCode = await JobQueueSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"JobQueueSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[11] Global AI Account Pool v1.1 Smoke Checks:");
        await RunCheckAsync(
            "Task T13a: Global AI Pool (10 Projects / 1 ChatGPT, 10 Videos / 4 MuseAI, Isolation & 8 Combinations)",
            async () =>
            {
                int exitCode = await GlobalAiPoolSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"GlobalAiPoolSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[12] Task Q1 ProviderAccount & Capacity Smoke Checks:");
        await RunCheckAsync(
            "Task Q1: ProviderAccount Bridge Commands, Slot Occupancy (No Duplicates) & Retry Budget (H1-H4, A03-A08, A13)",
            async () =>
            {
                int exitCode = await ProviderAccountAndCapacitySmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"ProviderAccountAndCapacitySmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[13] Browser Launch Performance Benchmark (6 Phases, Cold vs Warm, Bottlenecks):");
        await RunCheckAsync(
            "Browser Launch Benchmark: Cold/Warm Start, Window Appearance, DB Queries & Detached Navigation",
            async () =>
            {
                int exitCode = await BrowserLaunchBenchmark.RunAllBenchmarksAsync(sampleCount: 5);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"BrowserLaunchBenchmark failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[14] Task T16: Recovery, Retry, Cancel & Fault Injection Smoke Checks:");
        await RunCheckAsync(
            "Task T16: Crash Recovery, SideEffect Guard, Cancel Before/After Submit, Late Result Asset Preservation & Zombie Fencing",
            async () =>
            {
                await JobRecoverySmokeTests.RunAllAsync();
            });

        Console.WriteLine("\n[15] Task T16 Subagent A: Retry & Cancellation Policy Smoke Checks:");
        await RunCheckAsync(
            "Task T16-A: Finite Backoff, Attempts Preservation, Cancel Before/After Submit, Late Result Preservation & Snapshot Immutability",
            async () =>
            {
                int exitCode = await JobRetryAndCancelSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"JobRetryAndCancelSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[16] Task T16 Subagent B: Crash Recovery & Reconcile Engine Smoke Checks:");
        await RunCheckAsync(
            "Task T16-B: Startup & Periodic Sweep, Side-effect Boundary, Blind Resend Prevention, Occupancy Preservation & CAS Fencing",
            async () =>
            {
                int exitCode = await JobCrashRecoverySmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"JobCrashRecoverySmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[17] Task T16 Subagent C: Browser Session Checkpoint & Profile Isolation Smoke Checks:");
        await RunCheckAsync(
            "Task T16-C: Session Loss Isolation, Safe Quiescing, Visible Manual Login With Identity Probe, Return to Hidden & Auth Closed Guard",
            async () =>
            {
                await BrowserSessionCheckpointSmokeTests.RunAllAsync();
            });

        Console.WriteLine("\n[18] MuseAI Session & Identity Probe Smoke Checks:");
        await RunCheckAsync(
            "MuseAI: Real Profile Probe, Close & Restart App, Unauthenticated Guard, Timeout/Network, 2 Profiles Isolation, Stale Event Guard & Regression",
            async () =>
            {
                int exitCode = await MuseAiSessionProbeSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"MuseAiSessionProbeSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[19] AI Account Concurrency & Browser Lifecycle Smoke Checks:");
        await RunCheckAsync(
            "Account Concurrency & Browser Lifecycle: MaxTabs As Single N, Legacy Data Migration, Dynamic Capacity, On-Demand Browser & Session Persistence",
            async () =>
            {
                int exitCode = await AccountConcurrencyAndBrowserLifecycleSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"AccountConcurrencyAndBrowserLifecycleSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[20] Video Production from Images Workflow Smoke Checks:");
        await RunCheckAsync(
            "Video Workflow: 3 Input Modes, Optional Audio, Multi-scene Generation, §3.4 Re-run Matrix & Bridge Allow-list",
            async () =>
            {
                int exitCode = await VideoWorkflowSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"VideoWorkflowSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[21] Task T18a & T18d: Provider Registry & Workflow Definition Smoke Checks:");
        await RunCheckAsync(
            "Provider Registry & Workflow: Task Capabilities, NOT_VERIFIED Guard, Strict Dispatch, Linear Steps & Immutable Versioning",
            async () =>
            {
                int exitCode = await ProviderRegistryAndWorkflowSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"ProviderRegistryAndWorkflowSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[23] ChatGPT Session & Identity Probe Smoke Checks:");
        await RunCheckAsync(
            "ChatGPT: Real Profile Probe, Close & Restart App, Unauthenticated Guard, Timeout/Network, 2 Profiles Isolation, Stale Event Guard",
            async () =>
            {
                int exitCode = await ChatGptSessionProbeSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"ChatGptSessionProbeSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n[24] TikTok Session & Identity Probe Smoke Checks:");
        await RunCheckAsync(
            "TikTok: Real Profile Probe, Close & Restart App, Unauthenticated Guard, Timeout/Network, 2 Profiles Isolation, Stale Event Guard",
            async () =>
            {
                int exitCode = await TikTokSessionProbeSmokeTests.RunAllAsync();
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"TikTokSessionProbeSmokeTests failed with exit code {exitCode}");
                }
            });

        Console.WriteLine("\n=================================================================");
        if (failed == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"             ALL T05 SMOKE CHECKS PASSED ({passed}/{passed})                    ");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"             SMOKE CHECKS FAILED: {failed} failed, {passed} passed            ");
            Console.ResetColor();
            Console.WriteLine("=================================================================");
            return 1;
        }
    }
}
