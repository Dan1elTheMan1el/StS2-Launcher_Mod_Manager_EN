using System;
using System.Collections.Generic;
using System.Linq;
using STS2Mobile.Modding;
using STS2Mobile.Steam;

namespace WorkshopSyncTests;

// Standalone regression harness for WorkshopSyncService.ComputePlan's
// duplicate-pfid recovery (issue #70 fix 3). Not part of the APK build graph —
// see workshop-sync-tests.csproj for why it can reference the already-built
// STS2Mobile.dll directly instead of ProjectReference-ing STS2Mobile.csproj.
//
// Build/run:
//   dotnet build ../../src/STS2Mobile/STS2Mobile.csproj -c Release
//   dotnet run --project workshop-sync-tests.csproj
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        CaseA_DuplicatePfid_BothFoldersPresent_LowerTimeUpdatedOrphaned();
        CaseB_DuplicatePfid_LoserFolderMissing_GoesToStaleEntries();
        CaseC_NoDuplicates_RegressionUnaffected();

        if (_failures > 0)
        {
            Console.WriteLine($"\nFAILED: {_failures} assertion(s) failed.");
            return 1;
        }
        Console.WriteLine("\nAll workshop-sync-tests passed.");
        return 0;
    }

    // (a) Two entries share one pfid, both folders exist on disk. The lower
    // TimeUpdated one must be routed to Orphans (folder present -> deletable
    // after confirmation), and the winner (higher TimeUpdated) must be left
    // untouched by the subscription-driven loop.
    private static void CaseA_DuplicatePfid_BothFoldersPresent_LowerTimeUpdatedOrphaned()
    {
        const ulong pfid = 100;
        var entryOld = new ModConfigEntry
        {
            Id = "modA_old",
            Source = ModConfigEntry.SourceWorkshop,
            PublishedFileId = pfid,
            TimeUpdated = 1000,
            Order = 0,
        };
        var entryNew = new ModConfigEntry
        {
            Id = "modB_new",
            Source = ModConfigEntry.SourceWorkshop,
            PublishedFileId = pfid,
            TimeUpdated = 2000,
            Order = 1,
        };

        var installedFolderIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "modA_old",
            "modB_new",
        };
        var disabledFolderIds = new HashSet<string>(StringComparer.Ordinal);

        var subscriptions = new List<WorkshopItemDetails>
        {
            new WorkshopItemDetails
            {
                PublishedFileId = pfid,
                Title = "Item A/B",
                TimeUpdated = 2000,
                FileUrl = "https://example.invalid/x", // HasContent == true
            },
        };

        var plan = WorkshopSyncService.ComputePlan(
            subscriptions,
            new[] { entryOld, entryNew },
            installedFolderIds,
            disabledFolderIds: disabledFolderIds
        );

        Assert(
            "A: exactly one orphan",
            plan.Orphans.Count == 1,
            $"Orphans.Count={plan.Orphans.Count}"
        );
        Assert(
            "A: the OLD (lower TimeUpdated) entry is the orphan",
            plan.Orphans.Count == 1 && plan.Orphans[0].Id == "modA_old",
            plan.Orphans.Count == 1 ? $"got '{plan.Orphans[0].Id}'" : "n/a"
        );
        Assert(
            "A: no stale entries",
            plan.StaleEntries.Count == 0,
            $"StaleEntries.Count={plan.StaleEntries.Count}"
        );
        Assert(
            "A: winner is untouched — no install/update work generated",
            plan.ToInstall.Count == 0 && plan.ToUpdate.Count == 0,
            $"ToInstall={plan.ToInstall.Count} ToUpdate={plan.ToUpdate.Count}"
        );
    }

    // (b) Two entries share one pfid; the loser's folder is gone from disk
    // (neither Mods/ nor ModsDisabled/). It must be routed to StaleEntries
    // (nothing to delete — entry cleanup only), never Orphans.
    private static void CaseB_DuplicatePfid_LoserFolderMissing_GoesToStaleEntries()
    {
        const ulong pfid = 200;
        var entryGhost = new ModConfigEntry
        {
            Id = "modC_ghost",
            Source = ModConfigEntry.SourceWorkshop,
            PublishedFileId = pfid,
            TimeUpdated = 500,
            Order = 0,
        };
        var entryReal = new ModConfigEntry
        {
            Id = "modD_real",
            Source = ModConfigEntry.SourceWorkshop,
            PublishedFileId = pfid,
            TimeUpdated = 1500,
            Order = 1,
        };

        // Only the winner's folder exists on disk; the ghost entry's folder is
        // gone from both roots.
        var installedFolderIds = new HashSet<string>(StringComparer.Ordinal) { "modD_real" };
        var disabledFolderIds = new HashSet<string>(StringComparer.Ordinal);

        // Subscribed, matching the winner's TimeUpdated, so the winner itself
        // generates no install/update work and the test isolates the
        // duplicate-cleanup behavior.
        var subscriptions = new List<WorkshopItemDetails>
        {
            new WorkshopItemDetails
            {
                PublishedFileId = pfid,
                Title = "Item C/D",
                TimeUpdated = 1500,
                FileUrl = "https://example.invalid/y",
            },
        };

        var plan = WorkshopSyncService.ComputePlan(
            subscriptions,
            new[] { entryGhost, entryReal },
            installedFolderIds,
            disabledFolderIds: disabledFolderIds
        );

        Assert(
            "B: exactly one stale entry",
            plan.StaleEntries.Count == 1,
            $"StaleEntries.Count={plan.StaleEntries.Count}"
        );
        Assert(
            "B: the ghost (folder-missing) entry is the stale one",
            plan.StaleEntries.Count == 1 && plan.StaleEntries[0].Id == "modC_ghost",
            plan.StaleEntries.Count == 1 ? $"got '{plan.StaleEntries[0].Id}'" : "n/a"
        );
        Assert("B: no orphans", plan.Orphans.Count == 0, $"Orphans.Count={plan.Orphans.Count}");
        Assert(
            "B: winner is untouched — no install/update work generated",
            plan.ToInstall.Count == 0 && plan.ToUpdate.Count == 0,
            $"ToInstall={plan.ToInstall.Count} ToUpdate={plan.ToUpdate.Count}"
        );
    }

    // (c) No duplicate pfids at all — the pre-existing single-entry-per-pfid
    // plan must be unchanged (no accidental Orphan/StaleEntries noise, normal
    // ToUpdate detection still fires).
    private static void CaseC_NoDuplicates_RegressionUnaffected()
    {
        const ulong pfid = 300;
        var entry = new ModConfigEntry
        {
            Id = "modE",
            Source = ModConfigEntry.SourceWorkshop,
            PublishedFileId = pfid,
            TimeUpdated = 100,
            Order = 0,
        };

        var installedFolderIds = new HashSet<string>(StringComparer.Ordinal) { "modE" };
        var disabledFolderIds = new HashSet<string>(StringComparer.Ordinal);

        var subscriptions = new List<WorkshopItemDetails>
        {
            new WorkshopItemDetails
            {
                PublishedFileId = pfid,
                Title = "Item E",
                TimeUpdated = 200, // newer than the local entry -> expect ToUpdate
                FileUrl = "https://example.invalid/z",
            },
        };

        var plan = WorkshopSyncService.ComputePlan(
            subscriptions,
            new[] { entry },
            installedFolderIds,
            disabledFolderIds: disabledFolderIds
        );

        Assert(
            "C: normal update still detected",
            plan.ToUpdate.Count == 1 && plan.ToUpdate[0].PublishedFileId == pfid,
            $"ToUpdate.Count={plan.ToUpdate.Count}"
        );
        Assert(
            "C: no orphans introduced",
            plan.Orphans.Count == 0,
            $"Orphans.Count={plan.Orphans.Count}"
        );
        Assert(
            "C: no stale entries introduced",
            plan.StaleEntries.Count == 0,
            $"StaleEntries.Count={plan.StaleEntries.Count}"
        );
        Assert(
            "C: no install work (folder already present)",
            plan.ToInstall.Count == 0,
            $"ToInstall.Count={plan.ToInstall.Count}"
        );
    }

    private static void Assert(string name, bool condition, string detail)
    {
        if (condition)
        {
            Console.WriteLine($"PASS: {name}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"FAIL: {name} ({detail})");
        }
    }
}
