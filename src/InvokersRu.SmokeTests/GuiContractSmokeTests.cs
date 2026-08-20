using InvokersRu.Gui;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvokersRu.SmokeTests
{
    internal static class GuiContractSmokeTests
    {
        private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        private const string HashC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

        internal static void Run(Action<string> passed)
        {
            StrictPlanContractCheck();
            passed("GUI plan JSON parser accepts only a complete, internally consistent CLI contract");

            if (OperatingSystem.IsWindows())
            {
                RunnerTimeoutOwnChildOnlyCheck();
                passed("GUI companion timeout terminates only its own child process");
            }
        }

        private static void StrictPlanContractCheck()
        {
            JsonObject ready = ReadyResponse();
            CliPlanResult parsed = Parse(ready, 0);
            Require(parsed.CanApply && parsed.Catalog.ExactMatch
                && parsed.Observed.GameVersion == "0.60.1247",
                "A valid exact ready-to-apply response was rejected or lost its observed/catalog identity.");

            JsonObject catalogMissing = Clone(ready);
            catalogMissing["catalog"] = new JsonObject
            {
                ["present"] = false,
                ["regular_file"] = false,
                ["sha256"] = null,
                ["exact_match"] = false
            };
            catalogMissing["plan"] = "REFUSE_MISSING_OR_MISMATCHED_CATALOG";
            catalogMissing["can_apply"] = false;
            Require(!Parse(catalogMissing, 0).CanApply,
                "A coherent missing-catalog refusal was not accepted for display.");

            JsonObject conflict = Clone(ready);
            conflict["process_conflicts"] = new JsonArray("Invokers (1234; C:\\Game\\Invokers.exe)");
            conflict["plan"] = "REFUSE_CLOSE_GAME_AND_LAUNCHER";
            conflict["can_apply"] = false;
            Require(!Parse(conflict, 0).CanApply,
                "A coherent running-game refusal was not accepted for display.");

            JsonObject patched = Clone(ready);
            patched["status"] = "PatchedByThisTool";
            patched["message"] = "Already patched.";
            patched["plan"] = "NOOP_OR_RESTORE";
            patched["can_apply"] = false;
            patched["can_restore"] = true;
            patched["state"] = StateObject();
            Require(Parse(patched, 0).CanRestore,
                "A valid patched state was not accepted for restore.");

            JsonObject recovery = Clone(ready);
            recovery["status"] = "RecoveryRequired";
            recovery["message"] = "Recovery required.";
            recovery["plan"] = "RECOVERY_REQUIRED";
            recovery["can_apply"] = false;
            recovery["can_recover"] = true;
            recovery["journal"] = new JsonObject
            {
                ["operation"] = "runtime-cache-apply",
                ["phase"] = "ReplacementCommitted",
                ["transaction_id"] = "0123456789abcdef0123456789abcdef"
            };
            Require(Parse(recovery, 5).CanRecover,
                "RecoveryRequired with the CLI's intentional exit code 5 was rejected.");

            JsonObject unknownMember = Clone(ready);
            unknownMember["unexpected"] = true;
            ExpectInvalid(unknownMember, 0, "unknown top-level member");

            string duplicate = ready.ToJsonString();
            duplicate = duplicate.Insert(duplicate.LastIndexOf('}'), ",\"can_apply\":false");
            ExpectInvalid(duplicate, 0, "duplicate member");

            JsonObject nullProfile = Clone(ready);
            nullProfile["profile"] = null;
            ExpectInvalid(nullProfile, 0, "null profile");

            JsonObject nullObserved = Clone(ready);
            nullObserved["observed"] = null;
            ExpectInvalid(nullObserved, 0, "null observed identity");

            JsonObject nullCatalog = Clone(ready);
            nullCatalog["catalog"] = null;
            ExpectInvalid(nullCatalog, 0, "null catalog identity");

            JsonObject nullProcesses = Clone(ready);
            nullProcesses["process_conflicts"] = null;
            ExpectInvalid(nullProcesses, 0, "null process list");

            JsonObject nullMessage = Clone(ready);
            nullMessage["message"] = null;
            ExpectInvalid(nullMessage, 0, "null required scalar");

            JsonObject missingWriteFlag = Clone(ready);
            missingWriteFlag.Remove("installation_writes_enabled");
            ExpectInvalid(missingWriteFlag, 0, "missing write-enabled flag");

            JsonObject missingProcesses = Clone(ready);
            missingProcesses.Remove("process_conflicts");
            ExpectInvalid(missingProcesses, 0, "missing process list");

            JsonObject missingObservedVersion = Clone(ready);
            missingObservedVersion["observed"]!.AsObject().Remove("game_version");
            ExpectInvalid(missingObservedVersion, 0, "missing observed version member");

            JsonObject missingCatalogPresence = Clone(ready);
            Catalog(missingCatalogPresence).Remove("present");
            ExpectInvalid(missingCatalogPresence, 0, "missing catalog presence member");

            JsonObject missingCertifiedFlag = Clone(ready);
            Profile(missingCertifiedFlag).Remove("certified");
            ExpectInvalid(missingCertifiedFlag, 0, "missing profile certification member");

            JsonObject wrongExit = Clone(ready);
            ExpectInvalid(wrongExit, 5, "contradictory exit code");

            JsonObject falsePermission = Clone(ready);
            falsePermission["can_apply"] = false;
            ExpectInvalid(falsePermission, 0, "contradictory apply permission");

            JsonObject writesDisabled = Clone(ready);
            writesDisabled["installation_writes_enabled"] = false;
            ExpectInvalid(writesDisabled, 0, "write-disabled ready action");

            JsonObject badCount = Clone(ready);
            Profile(badCount)["english_fallbacks"] = 2;
            ExpectInvalid(badCount, 0, "composition sum mismatch");

            JsonObject negativeCount = Clone(ready);
            Profile(negativeCount)["base_fallbacks"] = -1;
            ExpectInvalid(negativeCount, 0, "negative certified fallback");

            JsonObject tooManyReviewFallbacks = Clone(ready);
            Profile(tooManyReviewFallbacks)["needs_review_fallbacks"] = 2;
            ExpectInvalid(tooManyReviewFallbacks, 0, "review fallback outside English fallback");

            JsonObject catalogMismatchClaimedExact = Clone(ready);
            Catalog(catalogMismatchClaimedExact)["sha256"] = HashB;
            ExpectInvalid(catalogMismatchClaimedExact, 0, "false exact catalog claim");

            JsonObject exactCatalogClaimedMismatch = Clone(ready);
            Catalog(exactCatalogClaimedMismatch)["exact_match"] = false;
            exactCatalogClaimedMismatch["plan"] = "REFUSE_MISSING_OR_MISMATCHED_CATALOG";
            exactCatalogClaimedMismatch["can_apply"] = false;
            ExpectInvalid(exactCatalogClaimedMismatch, 0, "exact catalog hash claimed as mismatch");

            JsonObject missingCatalogStillReady = Clone(catalogMissing);
            missingCatalogStillReady["plan"] = "READY_TO_APPLY";
            missingCatalogStillReady["can_apply"] = true;
            ExpectInvalid(missingCatalogStillReady, 0, "ready action without catalog");

            JsonObject conflictStillWritable = Clone(conflict);
            conflictStillWritable["can_apply"] = true;
            ExpectInvalid(conflictStillWritable, 0, "apply permission with running process");

            JsonObject patchedWithoutState = Clone(patched);
            patchedWithoutState["state"] = null;
            ExpectInvalid(patchedWithoutState, 0, "restore permission without state");

            JsonObject incompletePatchedState = Clone(patched);
            incompletePatchedState["state"]!.AsObject().Remove("build_id");
            ExpectInvalid(incompletePatchedState, 0, "incomplete installed state");

            JsonObject mismatchedPatchedState = Clone(patched);
            mismatchedPatchedState["state"]!["build_id"] = "different-runtime-cache-profile";
            ExpectInvalid(mismatchedPatchedState, 0, "installed state from another profile");

            JsonObject recoveryWithoutJournal = Clone(recovery);
            recoveryWithoutJournal["journal"] = null;
            ExpectInvalid(recoveryWithoutJournal, 5, "recovery permission without journal");

            JsonObject incompleteRecoveryJournal = Clone(recovery);
            incompleteRecoveryJournal["journal"]!.AsObject().Remove("phase");
            ExpectInvalid(incompleteRecoveryJournal, 5, "incomplete recovery journal");

            JsonObject unknownRecoveryPhase = Clone(recovery);
            unknownRecoveryPhase["journal"]!["phase"] = "FutureUnsafePhase";
            ExpectInvalid(unknownRecoveryPhase, 5, "unknown recovery phase");

            JsonObject unknownStatus = Clone(ready);
            unknownStatus["status"] = "FutureStatus";
            ExpectInvalid(unknownStatus, 0, "unknown status");

            JsonObject unknownAction = Clone(ready);
            unknownAction["plan"] = "FUTURE_ACTION";
            ExpectInvalid(unknownAction, 0, "unknown action");
        }

        private static void RunnerTimeoutOwnChildOnlyCheck()
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ping = Path.Combine(systemDirectory, "PING.EXE");
            Require(File.Exists(ping), $"Windows ping executable is missing: {ping}");

            string root = Path.Combine(Path.GetTempPath(), $"invokersru-gui-runner-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string companion = Path.Combine(root, CliRunner.CliFileName);
            File.Copy(ping, companion, overwrite: false);
            Process? unrelated = null;
            try
            {
                unrelated = Process.Start(new ProcessStartInfo
                {
                    FileName = ping,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "127.0.0.1", "-n", "30", "-w", "1000" }
                }) ?? throw new InvalidOperationException("Could not start the unrelated timeout sentinel process.");

                var runner = new CliRunner(root, TimeSpan.FromMilliseconds(500));
                bool timedOut = false;
                try
                {
                    _ = runner.RunAsync("127.0.0.1", new[] { "-n", "30", "-w", "1000" })
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("не завершил операцию", StringComparison.Ordinal))
                {
                    timedOut = true;
                }

                Require(timedOut, "A non-terminating companion did not return the bounded Russian timeout error.");
                Require(!unrelated.HasExited,
                    "Timing out the companion terminated an unrelated process with the same executable image.");
            }
            finally
            {
                if (unrelated != null)
                {
                    try
                    {
                        if (!unrelated.HasExited)
                        {
                            unrelated.Kill(entireProcessTree: true);
                            unrelated.WaitForExit(5_000);
                        }
                    }
                    finally
                    {
                        unrelated.Dispose();
                    }
                }

                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static JsonObject ReadyResponse()
        {
            return new JsonObject
            {
                ["schema"] = 1,
                ["patcher_version"] = "3.0.0.0",
                ["installation_writes_enabled"] = true,
                ["status"] = "CompatibleOriginal",
                ["message"] = "Exact compatible original.",
                ["cache_root"] = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "invokersru-contract-cache")),
                ["observed"] = new JsonObject
                {
                    ["english_sha256"] = HashA,
                    ["base_sha256"] = HashB,
                    ["stamp_sha256"] = HashC,
                    ["game_version"] = "0.60.1247",
                    ["english_content"] = "Prod_0.60.0_68",
                    ["base_content"] = "Prod_0.60.0_68"
                },
                ["catalog"] = new JsonObject
                {
                    ["present"] = true,
                    ["regular_file"] = true,
                    ["sha256"] = HashA,
                    ["exact_match"] = true
                },
                ["profile"] = new JsonObject
                {
                    ["id"] = "runtime-cache-win64-0.60.1247-prod68",
                    ["game_version"] = "0.60.1247",
                    ["readiness"] = "ready",
                    ["certified"] = true,
                    ["translation_policy"] = "community-preview-all-drafts",
                    ["catalog_sha256"] = HashA,
                    ["expected_output_sha256"] = HashB,
                    ["entry_count"] = 41_292,
                    ["applied_translations"] = 41_037,
                    ["english_fallbacks"] = 1,
                    ["base_fallbacks"] = 254,
                    ["needs_review_fallbacks"] = 0
                },
                ["state"] = null,
                ["journal"] = null,
                ["process_conflicts"] = new JsonArray(),
                ["plan"] = "READY_TO_APPLY",
                ["can_apply"] = true,
                ["can_restore"] = false,
                ["can_recover"] = false
            };
        }

        private static JsonObject StateObject()
        {
            return new JsonObject
            {
                ["build_id"] = "runtime-cache-win64-0.60.1247-prod68",
                ["applied_translations"] = 41_037,
                ["applied_at"] = "2026-08-20T18:26:44Z",
                ["patched_sha256"] = HashB,
                ["original_sha256"] = HashC
            };
        }

        private static CliPlanResult Parse(JsonObject response, int exitCode)
        {
            return Parse(response.ToJsonString(), exitCode);
        }

        private static CliPlanResult Parse(string response, int exitCode)
        {
            return CliPlanResult.Parse(new CliCommandResult(exitCode, response, string.Empty));
        }

        private static void ExpectInvalid(JsonObject response, int exitCode, string label)
        {
            ExpectInvalid(response.ToJsonString(), exitCode, label);
        }

        private static void ExpectInvalid(string response, int exitCode, string label)
        {
            try
            {
                _ = Parse(response, exitCode);
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException($"GUI plan parser accepted {label}.");
        }

        private static JsonObject Clone(JsonObject value)
        {
            return value.DeepClone().AsObject();
        }

        private static JsonObject Profile(JsonObject response)
        {
            return response["profile"]?.AsObject()
                ?? throw new InvalidOperationException("Test response has no profile object.");
        }

        private static JsonObject Catalog(JsonObject response)
        {
            return response["catalog"]?.AsObject()
                ?? throw new InvalidOperationException("Test response has no catalog object.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
