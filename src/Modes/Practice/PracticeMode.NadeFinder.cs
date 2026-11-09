using System.Diagnostics;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using FragLab.Core;
using FragLab.Core.Menus;
using CsVector = CounterStrikeSharp.API.Modules.Utils.Vector;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace FragLab.Modes.Practice;

/// <summary>
/// Nade finder: stand where you want to throw from (!nfthrow), stand where it should land (!nfland),
/// then !nffind searches standing and jump-throws by simulating them through the map and puts you on
/// the best lineup. Lineups that still work with slightly-off aim are preferred over pixel-perfect ones.
/// The search runs a few milliseconds per tick so the server never stalls.
/// !nfcheck compares the simulation with your real throws to show how accurate it is.
/// </summary>
public sealed partial class PracticeMode
{
    private const float EyeHeight = 64f;
    private const double SearchBudgetMs = 3.0;

    // How far off the aim may be (degrees) while the lineup still has to land within the accuracy.
    private const float AimSlack = 0.35f;

    // Lineups must land between 5 and 50 units from the chosen spot.
    private const float MinTolerance = 5f;
    private const float MaxTolerance = 50f;

    /// <summary>Accuracy choices in units; null = NadeFinderTolerance from the config (Normal).</summary>
    private static readonly (string Name, float? Units)[] AccuracyLevels =
    [
        ("Normal", null),
        ("Precise", 10f),
        ("Exact", 5f),
        ("Loose", 50f)
    ];

    private readonly Dictionary<int, FinderState> _finder = new();
    private readonly HashSet<int> _nfCheck = new();
    private readonly Dictionary<(int Slot, string Grenade), Queue<CheckEntry>> _predictions = new();
    private readonly Dictionary<int, CheckEntry> _awaitingRelease = new();
    private readonly List<CheckEntry> _tracking = new();
    private SearchJob? _search;
    private Timer? _searchTimer;

    private sealed class FinderState
    {
        public Vector3? ThrowFeet;
        public Vector3? Target;
        public NadeKind Kind = NadeKind.Smoke;
        public int Accuracy;
        public FinderResult? Result;
    }

    private sealed record FinderResult(Vector3 Feet, float Pitch, float Yaw, ThrowType Type, NadeKind Kind, float Error, bool Forgiving);

    private readonly record struct Throw(float Pitch, float Yaw, ThrowType Type, int Group);

    private sealed class SearchJob
    {
        public required int Slot;
        public required ulong SteamId;
        public required NadeKind Kind;
        public required Vector3 Feet;
        public required Vector3 Target;
        public required float Tolerance;
        public required NadeSimulator Simulator;
        public List<Throw> Queue = [];
        public int Index;
        public int Phase = 1;
        public readonly List<(Throw Throw, float Error)> Results = [];
        public int LastReportedPercent;
    }

    private void RegisterNadeFinder()
    {
        AddCommand(["nf", "nadefinder"], "Open the nade finder", (p, _) => WithPractice(p, OpenFinderMenu));
        AddCommand(["nfthrow"], "Nade finder: throw from where you stand", (p, _) => WithPractice(p, SetThrowSpot));
        AddCommand(["nfland"], "Nade finder: the grenade should land where you stand", (p, _) => WithPractice(p, SetLandingSpot));
        AddCommand(["nffind"], "Nade finder: search for a lineup", (p, _) => WithPractice(p, StartSearch));
        AddCommand(["nfgo"], "Nade finder: go to the found lineup", (p, _) => WithPractice(p, GoToFinderResult));
        AddCommand(["nfsave"], "Save the found lineup: !nfsave <name>", OnFinderSave, adminOnly: true);
        AddCommand(["nfcheck"], "Toggle comparing the nade finder's prediction with your real throws", (p, _) => WithPractice(p, ToggleFinderCheck));

        LoadLearnedCollision();
        LoadLearnedJump();
        Plugin.RegisterListener<Listeners.OnEntitySpawned>(entity =>
        {
            if (!IsActive || !entity.DesignerName.EndsWith("_projectile"))
                return;
            Server.NextFrame(() =>
            {
                LearnCollision(entity);
                LearnJumpThrow(entity);
                if (_nfCheck.Count > 0)
                    OnProjectileSpawned(entity);
            });
        });

        // Follow real grenades tick by tick so their path can be compared with the simulation.
        Plugin.RegisterListener<Listeners.OnTick>(TrackRealGrenades);
    }

    private FinderState Finder(CCSPlayerController player)
    {
        if (!_finder.TryGetValue(player.Slot, out var state))
            _finder[player.Slot] = state = new FinderState();
        return state;
    }

    /// <summary>How close the lineup must land, always between MinTolerance and MaxTolerance units.</summary>
    private float Tolerance(FinderState state)
    {
        var (_, units) = AccuracyLevels[state.Accuracy];
        return Math.Clamp(units ?? Plugin.Config.Practice.NadeFinderTolerance, MinTolerance, MaxTolerance);
    }

    private void OpenFinderMenu(CCSPlayerController player)
    {
        Plugin.Menus.Open(player, new Menu("Nade finder", menu =>
        {
            var state = Finder(player);
            menu.Add($"Grenade: {state.Kind}  (select to change)", p =>
            {
                var s = Finder(p);
                s.Kind = (NadeKind)(((int)s.Kind + 1) % Enum.GetValues<NadeKind>().Length);
                s.Result = null;
            }, "#00FF7F");
            menu.Add($"Accuracy: {AccuracyLevels[state.Accuracy].Name} (within {Tolerance(state):0} units)", p =>
            {
                var s = Finder(p);
                s.Accuracy = (s.Accuracy + 1) % AccuracyLevels.Length;
            }, "#00FF7F");
            menu.Add("1. Throw from here" + (state.ThrowFeet != null ? " [x]" : ""), SetThrowSpot);
            menu.Add("2. Land here" + (state.Target != null ? " [x]" : ""), SetLandingSpot);
            menu.Add(_search != null ? "Searching..." : "3. Find lineup", StartSearch, "#FFD700");
            if (state.Result != null)
                menu.Add($"Go to lineup ({NadeSimulator.Describe(state.Result.Type)})", GoToFinderResult, "#5EB8FF");
            menu.Add("Close menu (!nf reopens)", p => Plugin.Menus.Close(p), "#888888");
        }) { AllowMovement = true });
    }

    private void SetThrowSpot(CCSPlayerController player)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        if (origin == null || !player.PawnIsAlive)
            return;
        var state = Finder(player);
        state.ThrowFeet = new Vector3(origin.X, origin.Y, origin.Z);
        state.Result = null;
        Chat.To(player, "Throw spot set. Now stand where the grenade should land and select {green}Land here{default} (!nfland).");
    }

    private void SetLandingSpot(CCSPlayerController player)
    {
        var origin = player.PlayerPawn.Value?.AbsOrigin;
        if (origin == null || !player.PawnIsAlive)
            return;
        var state = Finder(player);
        state.Target = new Vector3(origin.X, origin.Y, origin.Z);
        state.Result = null;
        Chat.To(player, "Landing spot set. Select {green}Find lineup{default} (!nffind).");
    }

    // ---------------------------------------------------------------- search

    private void StartSearch(CCSPlayerController player)
    {
        var state = Finder(player);
        if (state.ThrowFeet is not { } feet || state.Target is not { } target)
        {
            Chat.To(player, "Set both spots first: {green}!nfthrow{default} where you throw from, {green}!nfland{default} where it should land.");
            return;
        }

        if (_search != null)
        {
            Chat.To(player, "Another search is running, try again in a moment.");
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null)
            return;

        var job = new SearchJob
        {
            Slot = player.Slot,
            SteamId = player.SteamID,
            Kind = state.Kind,
            Feet = feet,
            Target = target,
            Tolerance = Tolerance(state),
            Simulator = new NadeSimulator(pawn)
        };

        // Phase 1: coarse sweep around the direction of the target, standing throws and jump-throws.
        var bearing = MathF.Atan2(target.Y - feet.Y, target.X - feet.X) * 180f / MathF.PI;
        foreach (var type in Enum.GetValues<ThrowType>())
            for (var yaw = -30f; yaw <= 30f; yaw += 1.5f)
                for (var pitch = -88f; pitch <= 60f; pitch += 1.5f)
                    job.Queue.Add(new Throw(pitch, NormalizeYaw(bearing + yaw), type, 0));

        _search = job;
        state.Result = null;
        _searchTimer = Plugin.AddTimer(0.01f, StepSearch, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        Chat.To(player, $"Searching {job.Kind} lineups (left, middle, right, jump and jump-middle throws)...");
        if (!NadeSimulator.HasLearnedCollision)
            Chat.To(player, "Tip: throw any grenade once in practice so the finder learns exactly what grenades bounce off on this server.");
    }

    private void StepSearch()
    {
        var job = _search;
        if (job == null)
        {
            StopSearch();
            return;
        }

        var player = Utilities.GetPlayerFromSlot(job.Slot);
        if (!IsActive || _dryRun || !Players.IsValid(player) || player!.SteamID != job.SteamId)
        {
            StopSearch();
            return;
        }

        var eye = job.Feet with { Z = job.Feet.Z + EyeHeight };
        var watch = Stopwatch.StartNew();
        while (job.Index < job.Queue.Count && watch.Elapsed.TotalMilliseconds < SearchBudgetMs)
        {
            var t = job.Queue[job.Index++];
            var landing = job.Simulator.Simulate(eye, t.Pitch, t.Yaw, t.Type, job.Kind);
            job.Results.Add((t, NadeSimulator.Distance(landing, job.Target, job.Kind)));
        }

        if (job.Phase == 1)
        {
            var percent = job.Index * 100 / Math.Max(1, job.Queue.Count);
            if (percent >= job.LastReportedPercent + 25 && percent < 100)
            {
                job.LastReportedPercent = percent - percent % 25;
                Chat.To(player, $"Nade finder: {job.LastReportedPercent}%...");
            }
        }

        if (job.Index < job.Queue.Count)
            return;

        switch (job.Phase)
        {
            case 1:
                NextPhase(job, RefineAround(job, take: 16));
                break;
            case 2:
                NextPhase(job, FineAround(job, take: 6));
                break;
            case 3:
                NextPhase(job, StabilityChecks(job, take: 12));
                break;
            default:
                FinishSearch(player, job);
                break;
        }
    }

    private static void NextPhase(SearchJob job, List<Throw> queue)
    {
        job.Phase++;
        job.Index = 0;
        job.Queue = queue;
    }

    /// <summary>Phase 2: fine grid around the best distinct candidates.</summary>
    private static List<Throw> RefineAround(SearchJob job, int take)
    {
        var best = job.Results.OrderBy(r => r.Error)
            .DistinctBy(r => (r.Throw.Type, MathF.Round(r.Throw.Pitch / 6f), MathF.Round(r.Throw.Yaw / 6f)))
            .Take(take);

        var queue = new List<Throw>();
        foreach (var (c, _) in best)
            for (var dy = -1.5f; dy <= 1.5f; dy += 0.25f)
                for (var dp = -1.5f; dp <= 1.5f; dp += 0.25f)
                    queue.Add(new Throw(Math.Clamp(c.Pitch + dp, -89f, 89f), NormalizeYaw(c.Yaw + dy), c.Type, 0));
        return queue;
    }

    /// <summary>Phase 3: a very fine grid (0.03 degrees) around the best few, so lineups can land within a few units.</summary>
    private static List<Throw> FineAround(SearchJob job, int take)
    {
        var best = job.Results.OrderBy(r => r.Error)
            .DistinctBy(r => (r.Throw.Type, MathF.Round(r.Throw.Pitch), MathF.Round(r.Throw.Yaw)))
            .Take(take);

        var queue = new List<Throw>();
        foreach (var (c, _) in best)
            for (var dy = -0.3f; dy <= 0.3001f; dy += 0.03f)
                for (var dp = -0.3f; dp <= 0.3001f; dp += 0.03f)
                    queue.Add(new Throw(Math.Clamp(c.Pitch + dp, -89f, 89f), NormalizeYaw(c.Yaw + dy), c.Type, 0));
        return queue;
    }

    /// <summary>
    /// Phase 4: re-throw the best candidates with slightly-off aim. Each candidate gets its own group so its
    /// worst result can be compared; that is what makes a lineup forgiving instead of pixel-perfect.
    /// </summary>
    private static List<Throw> StabilityChecks(SearchJob job, int take)
    {
        var best = job.Results.Where(r => r.Error <= job.Tolerance)
            .OrderBy(r => r.Error)
            .DistinctBy(r => (r.Throw.Type, MathF.Round(r.Throw.Pitch), MathF.Round(r.Throw.Yaw)))
            .Take(take)
            .ToList();
        if (best.Count == 0)
            best = job.Results.OrderBy(r => r.Error).Take(1).ToList();

        job.Results.Clear();
        var queue = new List<Throw>();
        for (var group = 0; group < best.Count; group++)
        {
            var c = best[group].Throw;
            queue.Add(c with { Group = group + 1 });
            foreach (var (dp, dy) in new[] { (AimSlack, 0f), (-AimSlack, 0f), (0f, AimSlack), (0f, -AimSlack) })
                queue.Add(new Throw(Math.Clamp(c.Pitch + dp, -89f, 89f), NormalizeYaw(c.Yaw + dy), c.Type, group + 1));
        }
        return queue;
    }

    private void FinishSearch(CCSPlayerController player, SearchJob job)
    {
        StopSearch();

        // Per candidate: its own error (first entry of the group) and the worst error with off aim.
        var candidates = job.Results.GroupBy(r => r.Throw.Group)
            .Select(g => (Center: g.First(), Worst: g.Max(r => r.Error)))
            .ToList();
        if (candidates.Count == 0)
        {
            Chat.To(player, "{red}Not possible{default} from this spot.");
            return;
        }

        // Among the lineups that work, prefer one whose aim point is on an edge (a corner or brick edge is easy to find again).
        var forgiving = candidates.Where(c => c.Worst <= job.Tolerance)
            .OrderByDescending(c => IsEdgeAim(job.Simulator, job.Feet, c.Center.Throw.Pitch, c.Center.Throw.Yaw))
            .ThenBy(c => c.Worst)
            .FirstOrDefault();
        var pick = forgiving != default ? forgiving : candidates.OrderBy(c => c.Center.Error).First();

        if (pick.Center.Error > job.Tolerance)
        {
            Chat.To(player, $"{{red}}Not possible{{default}} from this spot (closest: {pick.Center.Error:0} units away). " +
                            "Try another throw spot or a looser accuracy.");
            return;
        }

        var t = pick.Center.Throw;
        var result = new FinderResult(job.Feet, t.Pitch, t.Yaw, t.Type, job.Kind, pick.Center.Error, forgiving != default);
        Finder(player).Result = result;

        Chat.To(player, $"{{green}}Lineup found!{{default}} {NadeSimulator.Describe(t.Type)}, {pick.Center.Error:0} units from your spot" +
                        (result.Forgiving ? ", still lands with slightly-off aim." : ". {red}Needs exact aim.{default}"));
        Chat.To(player, "Save it with {green}!nfsave <name>{default}.");
        GoToFinderResult(player);
    }

    private void StopSearch()
    {
        _searchTimer?.Kill();
        _searchTimer = null;
        _search = null;
    }

    private void GoToFinderResult(CCSPlayerController player)
    {
        if (Finder(player).Result is not { } result)
        {
            Chat.To(player, "No lineup found yet. Use {green}!nffind{default} first.");
            return;
        }

        Plugin.Menus.Close(player);
        SetNoclip(player, false);
        Teleport(player, new CsVector(result.Feet.X, result.Feet.Y, result.Feet.Z), new QAngle(result.Pitch, result.Yaw, 0));

        var grenade = GrenadeClass(result.Kind, player.Team);
        var owned = player.PlayerPawn.Value?.WeaponServices?.MyWeapons.Any(w => w.Value?.DesignerName == grenade) == true;
        if (!owned)
            player.GiveNamedItem(grenade);
        Server.NextFrame(() => player.ExecuteClientCommandFromServer($"use {grenade}"));

        player.PrintToCenter($"{NadeSimulator.Describe(result.Type).ToUpperInvariant()}\nDon't move your mouse");
        Chat.To(player, $"Don't move your mouse: {{green}}{NadeSimulator.Describe(result.Type)}{{default}}" +
                        (NadeSimulator.IsJump(result.Type) ? " with a jump-throw bind." : ", standing still."));
    }

    private void OnFinderSave(CCSPlayerController? player, CommandInfo info)
    {
        WithPractice(player, p =>
        {
            var name = info.ArgString.Trim().Trim('"');
            if (Finder(p).Result is not { } result || name.Length == 0)
            {
                Chat.To(p, "Find a lineup first, then: !nfsave <name>");
                return;
            }

            Vector3? aim = p.PlayerPawn.Value is { } findPawn ? AimPoint(new NadeSimulator(findPawn), result.Feet, result.Pitch, result.Yaw) : null;

            var lineup = new NadeLineup
            {
                Name = name,
                Grenade = GrenadeClass(result.Kind, p.Team),
                Author = $"{p.PlayerName} (nade finder, {NadeSimulator.Describe(result.Type)})",
                X = result.Feet.X,
                Y = result.Feet.Y,
                Z = result.Feet.Z,
                Pitch = result.Pitch,
                Yaw = result.Yaw,
                HasAim = aim is not null,
                AimX = aim?.X ?? 0,
                AimY = aim?.Y ?? 0,
                AimZ = aim?.Z ?? 0
            };

            var existing = _lineups.Find(name);
            if (existing != null)
                _lineups.Lineups.Remove(existing);
            _lineups.Lineups.Add(lineup);
            if (TrySaveLineups(p))
                Chat.To(p, $"{{green}}Lineup '{name}' saved.{{default}} Find it in !nades.");
        });
    }

    private static string GrenadeClass(NadeKind kind, CsTeam team) => kind switch
    {
        NadeKind.Smoke => "weapon_smokegrenade",
        NadeKind.Flash => "weapon_flashbang",
        NadeKind.HE => "weapon_hegrenade",
        _ => team == CsTeam.CounterTerrorist ? "weapon_incgrenade" : "weapon_molotov"
    };

    private static float NormalizeYaw(float yaw)
    {
        while (yaw > 180f) yaw -= 360f;
        while (yaw <= -180f) yaw += 360f;
        return yaw;
    }

    // ---------------------------------------------------------------- accuracy check

    /// <summary>One real throw being compared with the simulation.</summary>
    private sealed class CheckEntry
    {
        public required NadeKind Kind;
        public required Vector3 Eye;
        public required float Pitch;
        public required float Yaw;
        public required (Vector3 Position, Vector3 Velocity) OurRelease;
        public required Vector3 OurLanding;
        public Vector3? RealReleaseLanding;

        public CBaseCSGrenadeProjectile? Projectile;
        public readonly List<Vector3> RealPath = [];
        public readonly List<int> RealBounces = [];
        public readonly List<Vector3> SimPath = [];
        public readonly List<SimBounce> SimBounces = [];
    }

    private void TrackRealGrenades()
    {
        if (_tracking.Count == 0)
            return;

        for (var i = _tracking.Count - 1; i >= 0; i--)
        {
            var entry = _tracking[i];
            var projectile = entry.Projectile;
            if (projectile == null || !projectile.IsValid || projectile.AbsOrigin == null || entry.RealPath.Count > 64 * 15)
            {
                _tracking.RemoveAt(i);
                continue;
            }

            entry.RealPath.Add(new Vector3(projectile.AbsOrigin.X, projectile.AbsOrigin.Y, projectile.AbsOrigin.Z));
            entry.RealBounces.Add(projectile.Bounces);
        }
    }

    private void ToggleFinderCheck(CCSPlayerController player)
    {
        var enable = _nfCheck.Add(player.Slot);
        if (!enable)
            _nfCheck.Remove(player.Slot);
        Chat.To(player, enable
            ? "{green}Nade finder check on:{default} throw with a LEFT click while standing still. Chat shows how the finder compares with your real throw."
            : "Nade finder check off.");
    }

    private static NadeKind? KindOf(string grenade) => grenade switch
    {
        "smokegrenade" => NadeKind.Smoke,
        "flashbang" => NadeKind.Flash,
        "hegrenade" => NadeKind.HE,
        "molotov" => NadeKind.Molotov,
        _ => null
    };

    /// <summary>Grenade thrown: simulate the same throw from the player's eye and aim.</summary>
    private void RecordPrediction(CCSPlayerController player, string grenade)
    {
        if (!_nfCheck.Contains(player.Slot) || KindOf(grenade) is not { } kind)
            return;

        var pawn = player.PlayerPawn.Value;
        if (pawn?.AbsOrigin == null)
            return;

        var viewHeight = pawn.ViewOffset.Z > 0 ? pawn.ViewOffset.Z : EyeHeight;
        var eye = new Vector3(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + viewHeight);
        var simulator = new NadeSimulator(pawn);

        // A jump-throw is predicted the way the finder would: from the standing eye on the floor below.
        var jumping = (pawn.AbsVelocity?.Z ?? 0) > 100f;
        if (jumping)
            eye = simulator.FloorBelow(eye) with { Z = simulator.FloorBelow(eye).Z - 2f + EyeHeight };
        var release = simulator.ThrowStart(eye, pawn.EyeAngles.X, pawn.EyeAngles.Y, jumping ? ThrowType.JumpLeft : ThrowType.Left);

        var entry = new CheckEntry
        {
            Kind = kind,
            Eye = eye,
            Pitch = pawn.EyeAngles.X,
            Yaw = pawn.EyeAngles.Y,
            OurRelease = release,
            OurLanding = simulator.Fly(release.Position, release.Velocity, kind)
        };

        var key = (player.Slot, grenade);
        if (!_predictions.TryGetValue(key, out var queue))
            _predictions[key] = queue = new Queue<CheckEntry>();
        queue.Enqueue(entry);
        _awaitingRelease[player.Slot] = entry;
    }

    /// <summary>
    /// The real grenade exists: compare its true release point and speed with our throw formula, and fly the
    /// real release through our physics, so a wrong formula and wrong bounce physics can be told apart.
    /// </summary>
    private void OnProjectileSpawned(CEntityInstance entity)
    {
        if (!entity.IsValid)
            return;

        var projectile = new CBaseCSGrenadeProjectile(entity.Handle);
        var thrower = projectile.Thrower.Value;
        if (thrower == null)
            return;

        var player = Players.Humans().FirstOrDefault(p => p.PlayerPawn.Value?.Handle == thrower.Handle);
        if (player == null || !_nfCheck.Contains(player.Slot) || !_awaitingRelease.Remove(player.Slot, out var entry))
            return;

        var realPos = new Vector3(projectile.InitialPosition.X, projectile.InitialPosition.Y, projectile.InitialPosition.Z);
        var realVel = new Vector3(projectile.InitialVelocity.X, projectile.InitialVelocity.Y, projectile.InitialVelocity.Z);
        entry.RealReleaseLanding = new NadeSimulator(thrower).Fly(realPos, realVel, entry.Kind, entry.SimPath, entry.SimBounces);
        entry.Projectile = projectile;
        _tracking.Add(entry);

        var (ourPos, ourVel) = entry.OurRelease;
        var startOff = Vector3.Distance(realPos, ourPos);
        var cos = Vector3.Dot(Vector3.Normalize(realVel), Vector3.Normalize(ourVel));
        var angleOff = MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;

        Chat.To(player, $"NF throw: start {startOff:0.0}u off | speed real {realVel.Length():0} vs ours {ourVel.Length():0} | direction {angleOff:0.0} deg off");
        Plugin.Logger.LogInformation(
            "NFCHECK throw {Kind} eye=({EX:0.00},{EY:0.00},{EZ:0.00}) aim=({P:0.00},{Yw:0.00}) real pos=({RX:0.00},{RY:0.00},{RZ:0.00}) vel=({VX:0.00},{VY:0.00},{VZ:0.00}) ours pos=({OX:0.00},{OY:0.00},{OZ:0.00}) vel=({WX:0.00},{WY:0.00},{WZ:0.00})",
            entry.Kind, entry.Eye.X, entry.Eye.Y, entry.Eye.Z, entry.Pitch, entry.Yaw,
            realPos.X, realPos.Y, realPos.Z, realVel.X, realVel.Y, realVel.Z,
            ourPos.X, ourPos.Y, ourPos.Z, ourVel.X, ourVel.Y, ourVel.Z);
    }

    /// <summary>Grenade landed/popped: compare with both predictions.</summary>
    private void ComparePrediction(CCSPlayerController player, string grenade, Vector3 actual)
    {
        if (!_predictions.TryGetValue((player.Slot, grenade), out var queue) || !queue.TryDequeue(out var entry))
            return;

        var tolerance = Math.Clamp(Plugin.Config.Practice.NadeFinderTolerance, MinTolerance, MaxTolerance);
        string Colored(float error) => $"{(error <= tolerance ? "{green}" : "{red}")}{error:0}u{{default}}";

        var finderError = NadeSimulator.Distance(entry.OurLanding, actual, entry.Kind);
        var physicsError = entry.RealReleaseLanding is { } real ? NadeSimulator.Distance(real, actual, entry.Kind) : float.NaN;

        Chat.To(player, $"NF landing: finder {Colored(finderError)} off" +
                        (float.IsNaN(physicsError) ? "" : $" | your real throw in our physics {Colored(physicsError)} off"));

        _tracking.Remove(entry);
        if (entry.RealPath.Count > 0 && entry.SimPath.Count > 0)
            ReportPathSplit(player, entry);
        Plugin.Logger.LogInformation(
            "NFCHECK land {Kind} actual=({AX:0.00},{AY:0.00},{AZ:0.00}) finder=({FX:0.00},{FY:0.00},{FZ:0.00}) err={FE:0.0} physicsErr={PE:0.0}",
            entry.Kind, actual.X, actual.Y, actual.Z, entry.OurLanding.X, entry.OurLanding.Y, entry.OurLanding.Z, finderError, physicsError);
    }
    /// <summary>
    /// Finds where the real and simulated flight paths split and says why: the real grenade hit something
    /// the simulation didn't (or the other way round), or they bounced differently.
    /// </summary>
    private void ReportPathSplit(CCSPlayerController player, CheckEntry entry)
    {
        const float splitDistance = 12f;
        var real = entry.RealPath;
        var sim = entry.SimPath;

        // Real samples start a tick or so after release; line the two paths up on their first stretch.
        var offset = Enumerable.Range(-1, 5)
            .Select(o => (Offset: o, Error: Enumerable.Range(0, Math.Min(8, real.Count))
                .Where(k => k + o >= 0 && k + o < sim.Count)
                .Select(k => Vector3.Distance(real[k], sim[k + o]))
                .DefaultIfEmpty(float.MaxValue)
                .Average()))
            .MinBy(x => x.Error).Offset;

        var split = -1;
        for (var k = 0; k < real.Count; k++)
        {
            var s = Math.Clamp(k + offset, 0, sim.Count - 1);
            if (Vector3.Distance(real[k], sim[s]) > splitDistance)
            {
                split = k;
                break;
            }
        }

        if (split < 0)
        {
            Chat.To(player, "NF path: real and simulated paths match the whole way.");
            Plugin.Logger.LogInformation("NFCHECK path match realTicks={R} simTicks={S} offset={O}", real.Count, sim.Count, offset);
            return;
        }

        var simTick = Math.Clamp(split + offset, 0, sim.Count - 1) + 1; // SimPath[i] is after tick i+1
        var from = Math.Max(1, split - 2);
        var realBounced = Enumerable.Range(from, Math.Max(0, Math.Min(3, entry.RealBounces.Count - from)))
            .Any(k => entry.RealBounces[k] > entry.RealBounces[k - 1]);
        var simBounce = entry.SimBounces.Where(b => Math.Abs(b.Tick - simTick) <= 2).Select(b => (SimBounce?)b).FirstOrDefault();

        var reason = (realBounced, simBounce != null) switch
        {
            (true, false) => "the real grenade hit something the finder didn't (a player/bot, prop or glass?)",
            (false, true) => $"the finder hit a surface the real grenade went through (surface facing {Facing(simBounce!.Value.Normal)})",
            (true, true) => $"both bounced, but differently (finder surface facing {Facing(simBounce!.Value.Normal)})",
            _ => "they slowly drifted apart (rolling or sliding)"
        };

        var at = real[split];
        Chat.To(player, $"NF path: split after {split / 64f:0.00}s - {reason}.");
        Plugin.Logger.LogInformation(
            "NFCHECK path split tick={K} offset={O} real=({RX:0.0},{RY:0.0},{RZ:0.0}) sim=({SX:0.0},{SY:0.0},{SZ:0.0}) realBounced={RB} simBounce={SB} realBounces={RBC} simBounces={SBC}",
            split, offset, at.X, at.Y, at.Z, sim[simTick - 1].X, sim[simTick - 1].Y, sim[simTick - 1].Z,
            realBounced, simBounce?.ToString() ?? "none",
            entry.RealBounces.LastOrDefault(), entry.SimBounces.Count);
    }

    private static string Facing(Vector3 normal) =>
        normal.Z > 0.7f ? "up (floor)" : normal.Z < -0.7f ? "down (ceiling)" : "sideways (wall)";

    private string CollisionFile => Path.Combine(Plugin.ModuleDirectory, "data", "grenade-collision.txt");

    /// <summary>A real grenade exists: remember exactly what grenades collide with, for the simulation.</summary>
    private void LearnCollision(CEntityInstance entity)
    {
        if (!entity.IsValid || !NadeSimulator.Learn(new CBaseEntity(entity.Handle)) || NadeSimulator.LearnedCollision is not { } learned)
            return;

        Plugin.Logger.LogInformation("Nade finder learned grenade collision: as={As:X} with={With:X} exclude={Exclude:X}",
            learned.As, learned.With, learned.Exclude);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CollisionFile)!);
            File.WriteAllText(CollisionFile, $"{learned.As} {learned.With} {learned.Exclude}");
        }
        catch (IOException)
        {
            // Learned again next time a grenade is thrown.
        }
    }

    private void LoadLearnedCollision()
    {
        try
        {
            if (!File.Exists(CollisionFile))
                return;
            var parts = File.ReadAllText(CollisionFile).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && ulong.TryParse(parts[0], out var a) && ulong.TryParse(parts[1], out var w) && ulong.TryParse(parts[2], out var e))
                NadeSimulator.LearnedCollision = (a, w, e);
        }
        catch (IOException)
        {
        }
    }

    private string JumpFile => Path.Combine(Plugin.ModuleDirectory, "data", "jump-throw.txt");

    /// <summary>
    /// Measures a real jump-throw: how much upward speed the jump adds and how much higher the grenade is
    /// released than a standing throw from the same spot. The finder then uses those numbers for jump-throws.
    /// </summary>
    private void LearnJumpThrow(CEntityInstance entity)
    {
        if (!entity.IsValid)
            return;

        var projectile = new CBaseCSGrenadeProjectile(entity.Handle);
        var pawn = projectile.Thrower.Value;
        if (pawn?.AbsOrigin == null || pawn.AbsVelocity == null)
            return;

        // Only clean jump-throws: going up, not running.
        var playerVel = new Vector3(pawn.AbsVelocity.X, pawn.AbsVelocity.Y, pawn.AbsVelocity.Z);
        if (playerVel.Z < 100f || new Vector2(playerVel.X, playerVel.Y).Length() > 20f)
            return;

        var realPos = new Vector3(projectile.InitialPosition.X, projectile.InitialPosition.Y, projectile.InitialPosition.Z);
        var realVel = new Vector3(projectile.InitialVelocity.X, projectile.InitialVelocity.Y, projectile.InitialVelocity.Z);
        var simulator = new NadeSimulator(pawn);
        var pitch = pawn.EyeAngles.X;
        var yaw = pawn.EyeAngles.Y;

        // How hard it was thrown follows from the sideways speed, which jumping doesn't change.
        var origin = new Vector3(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
        var floorEye = simulator.FloorBelow(origin) with { Z = simulator.FloorBelow(origin).Z - 2f + EyeHeight };
        var fullThrow = simulator.ThrowStart(floorEye, pitch, yaw, ThrowType.Left).Velocity;
        var sideways = new Vector2(fullThrow.X, fullThrow.Y).Length();
        if (sideways < 100f)
            return; // thrown almost straight up or down: can't tell the throw strength

        var ratio = new Vector2(realVel.X, realVel.Y).Length() / sideways;
        var type = ratio > 0.8f ? ThrowType.Left : ratio > 0.5f ? ThrowType.Middle : ThrowType.Right;
        if (type == ThrowType.Right)
            return;

        var standing = simulator.ThrowStart(floorEye, pitch, yaw, type);
        var upSpeed = realVel.Z - standing.Velocity.Z;
        var releaseHeight = realPos.Z - standing.Position.Z;
        if (upSpeed is < 0f or > 600f || releaseHeight is < -30f or > 100f)
            return;

        // Average with earlier measurements to smooth out timing differences between throws.
        if (NadeSimulator.LearnedJump is { } old)
        {
            upSpeed = (old.UpSpeed + upSpeed) / 2f;
            releaseHeight = (old.ReleaseHeight + releaseHeight) / 2f;
        }

        var changed = NadeSimulator.LearnedJump is not { } before ||
                      MathF.Abs(before.UpSpeed - upSpeed) > 5f || MathF.Abs(before.ReleaseHeight - releaseHeight) > 1f;
        NadeSimulator.LearnedJump = (upSpeed, releaseHeight);
        Plugin.Logger.LogInformation("Nade finder learned jump-throw: up={Up:0.0} u/s release={H:0.00} units", upSpeed, releaseHeight);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JumpFile)!);
            File.WriteAllText(JumpFile, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{upSpeed} {releaseHeight}"));
        }
        catch (IOException)
        {
        }

        var thrower = Players.Humans().FirstOrDefault(p => p.PlayerPawn.Value?.Handle == pawn.Handle);
        if (changed && thrower != null)
            Chat.To(thrower, $"Nade finder measured your jump-throw: {{green}}+{upSpeed:0} u/s{{default}} upward, released {releaseHeight:0.0} units higher. Jump-throw lineups now use this.");
    }

    private void LoadLearnedJump()
    {
        try
        {
            if (!File.Exists(JumpFile))
                return;
            var parts = File.ReadAllText(JumpFile).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length == 2 &&
                float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out var up) &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out var height))
                NadeSimulator.LearnedJump = (up, height);
        }
        catch (IOException)
        {
        }
    }

    private void ResetNadeFinder()
    {
        StopSearch();
        _finder.Clear();
        _nfCheck.Clear();
        _predictions.Clear();
        _awaitingRelease.Clear();
        _tracking.Clear();
    }
}
