using CounterStrikeSharp.API.Core;

namespace FragLab.Core;

/// <summary>
/// Prints each player's damage to and from every enemy when the round ends, e.g.
/// "To [80 / 2] From [27 / 1] - Name (73 hp)". Only runs when the active mode wants it.
/// </summary>
public sealed class DamageReport
{
    private readonly FragLabPlugin _plugin;
    private readonly Dictionary<(int Attacker, int Victim), (int Damage, int Hits)> _damage = new();
    private readonly Dictionary<int, int> _health = new();

    public DamageReport(FragLabPlugin plugin)
    {
        _plugin = plugin;
    }

    private bool Enabled => _plugin.Modes.Active?.ShowDamageReport == true;

    public void Register()
    {
        _plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _damage.Clear();
            _health.Clear();
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventPlayerHurt>((e, _) =>
        {

            if (!Enabled)
                return HookResult.Continue;

            var victim = e.Userid;
            var attacker = e.Attacker;
            if (!Players.IsValid(victim) || !Players.IsValid(attacker) || attacker!.Slot == victim!.Slot)
                return HookResult.Continue;

            // dmg_health can exceed what the victim had left; cap it at their remaining health.
            var before = _health.GetValueOrDefault(victim.Slot, 100);
            var dealt = Math.Clamp(e.DmgHealth, 0, before);
            _health[victim.Slot] = Math.Max(0, e.Health);

            var key = (attacker.Slot, victim.Slot);
            var (damage, hits) = _damage.GetValueOrDefault(key);
            _damage[key] = (damage + dealt, hits + 1);
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventRoundEnd>((e, _) =>
        {
            if (Enabled && Players.IsPlayingTeam((CounterStrikeSharp.API.Modules.Utils.CsTeam)e.Winner))
                PrintAll();
            return HookResult.Continue;
        });
    }

    private void PrintAll()
    {
        var playing = Players.Playing().ToList();
        foreach (var player in playing.Where(p => !p.IsBot))
        {
            var enemies = playing.Where(p => p.Team != player.Team).ToList();
            if (enemies.Count == 0)
                continue;

            Chat.To(player, "{grey}----- Damage report -----");
            foreach (var enemy in enemies)
            {
                var (toDmg, toHits) = _damage.GetValueOrDefault((player.Slot, enemy.Slot));
                var (fromDmg, fromHits) = _damage.GetValueOrDefault((enemy.Slot, player.Slot));
                var hp = enemy.PawnIsAlive ? Math.Max(0, enemy.PlayerPawn.Value?.Health ?? 0) : 0;

                Chat.To(player, $"{{green}}To{{default}} [{toDmg} / {toHits}] {{red}}From{{default}} [{fromDmg} / {fromHits}] - " +
                                $"{enemy.PlayerName} ({(hp > 0 ? $"{hp} hp" : "dead")})");
            }
        }
    }
}
