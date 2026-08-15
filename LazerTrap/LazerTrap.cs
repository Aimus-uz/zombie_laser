using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CS2TraceRay.Class;
using CS2TraceRay.Enum;
using static CounterStrikeSharp.API.Core.Listeners;

namespace LazerTrap;

public class LazerTrapConfig : BasePluginConfig
{
    // CsTeam.Terrorist = 2, CsTeam.CounterTerrorist = 3.
    // Default assumes T-side = zombies, like most CS2 infection/escape setups.
    // Flip to 3 if your mode makes CT the zombie side.
    [JsonPropertyName("zombie_team")]
    public int ZombieTeam { get; set; } = 2;

    [JsonPropertyName("damage")]
    public float Damage { get; set; } = 15f;

    [JsonPropertyName("damage_interval")]
    public float DamageInterval { get; set; } = 0.5f;

    [JsonPropertyName("knockback_force")]
    public float KnockbackForce { get; set; } = 350f;

    [JsonPropertyName("hit_radius")]
    public float HitRadius { get; set; } = 18f;

    [JsonPropertyName("beam_width")]
    public float BeamWidth { get; set; } = 1.2f;

    [JsonPropertyName("beam_color")]
    public string BeamColor { get; set; } = "255 40 40";

    [JsonPropertyName("hurt_sound")]
    public string HurtSound { get; set; } = "physics/electrical/electric_spark_str1_hard1";

    [JsonPropertyName("hurt_humans")]
    public bool HurtHumans { get; set; } = false;

    [JsonPropertyName("admin_flag")]
    public string AdminFlag { get; set; } = "@css/root";

    // ---- player-placed temporary lasers (available to everyone, no admin flag) ----

    [JsonPropertyName("player_place_enabled")]
    public bool PlayerPlaceEnabled { get; set; } = true;

    [JsonPropertyName("player_place_cmd")]
    public string PlayerPlaceCommands { get; set; } = "css_laser,css_lazer";

    [JsonPropertyName("player_place_cooldown")]
    public float PlayerPlaceCooldown { get; set; } = 20f;

    [JsonPropertyName("player_place_length")]
    public float PlayerPlaceLength { get; set; } = 250f;

    [JsonPropertyName("player_place_duration")]
    public float PlayerPlaceDuration { get; set; } = 15f;

    // if true, only the zombie-mode "human" side can place; if false, anyone can
    [JsonPropertyName("player_place_humans_only")]
    public bool PlayerPlaceHumansOnly { get; set; } = true;

    // ---- burning after a hit ----

    [JsonPropertyName("burn_ticks")]
    public int BurnTicks { get; set; } = 3;

    [JsonPropertyName("burn_damage_per_tick")]
    public int BurnDamagePerTick { get; set; } = 5;

    [JsonPropertyName("burn_tick_interval")]
    public float BurnTickInterval { get; set; } = 1f;

    [JsonPropertyName("burn_sound")]
    public string BurnSound { get; set; } = "ambient/fire/fire_med_loop1.wav";
}

public class TrapDef
{
    public float SX { get; set; }
    public float SY { get; set; }
    public float SZ { get; set; }
    public float EX { get; set; }
    public float EY { get; set; }
    public float EZ { get; set; }
}

public class LazerTrap : BasePlugin, IPluginConfig<LazerTrapConfig>
{
    public override string ModuleName => "LazerTrap";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "ZAADROT";
    public override string ModuleDescription => "Zombie-mode laser traps: damages and pushes back zombies that cross the beam";

    public LazerTrapConfig Config { get; set; } = new();

    private const string BeamSprite = "materials/sprites/laserbeam.vmat";

    private static string TrapsFile => Path.Combine(
        Server.GameDirectory, "csgo", "addons", "counterstrikesharp",
        "configs", "plugins", "LazerTrap", "traps.json");

    private readonly List<(Vector Start, Vector End, CEnvBeam? Beam)> _traps = new();
    private readonly List<(Vector Start, Vector End, CEnvBeam? Beam, float ExpireAt)> _tempTraps = new();
    private readonly Dictionary<int, float> _lastHit = new();
    private readonly Dictionary<int, System.Numerics.Vector3> _pending = new();
    private readonly Dictionary<int, float> _placeCooldown = new();

    private Color _beamColor = Color.Red;

    public void OnConfigParsed(LazerTrapConfig config)
    {
        Config = config;
        _beamColor = ParseColor(config.BeamColor);
    }

    public override void Load(bool hotReload)
    {
        AddCommand("css_lazertrap_a", "Mark point A at your current position", (p, i) => SetPoint(p, true));
        AddCommand("css_lazertrap_b", "Mark point B and spawn the trap between A and B", (p, i) => SetPoint(p, false));
        AddCommand("css_lazertrap_undo", "Remove the last created trap", (p, i) => Undo(p));
        AddCommand("css_lazertrap_clear", "Remove all traps on this map (memory only)", (p, i) => ClearAll(p));
        AddCommand("css_lazertrap_save", "Save current traps to traps.json for this map", (p, i) => Save(p));
        AddCommand("css_lazertrap_list", "Show how many traps are active", (p, i) => List(p));

        foreach (var name in Config.PlayerPlaceCommands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddCommand(name, "Place a temporary laser trap in front of you", (p, i) => PlacePlayerLaser(p));

        RegisterListener<OnMapStart>(_ => AddTimer(1.0f, LoadTrapsForCurrentMap));
        RegisterListener<OnTick>(OnTick);

        if (hotReload)
            AddTimer(1.0f, LoadTrapsForCurrentMap);
    }

    public override void Unload(bool hotReload)
    {
        ClearBeams();
    }

    // ---------- placement (walk to the spot, mark it — no world trace needed) ----------

    private void SetPoint(CCSPlayerController? player, bool isFirst)
    {
        if (!IsAdmin(player))
        {
            player?.PrintToChat(" \x02[LazerTrap]\x01 No permission.");
            return;
        }

        var origin = player?.PlayerPawn.Value?.AbsOrigin;
        if (player == null || origin == null)
            return;

        // chest height, so the beam reads as a trip-laser rather than sitting on the floor
        var point = new System.Numerics.Vector3(origin.X, origin.Y, origin.Z + 40f);

        if (isFirst)
        {
            _pending[player.Slot] = point;
            player.PrintToChat(" \x04[LazerTrap]\x01 Point A marked. Walk to point B and run css_lazertrap_b.");
            return;
        }

        if (!_pending.TryGetValue(player.Slot, out var a))
        {
            player.PrintToChat(" \x04[LazerTrap]\x01 Mark point A first with css_lazertrap_a.");
            return;
        }

        _pending.Remove(player.Slot);
        var start = new Vector(a.X, a.Y, a.Z);
        var end = new Vector(point.X, point.Y, point.Z);
        var beam = CreateBeam(start, end);
        _traps.Add((start, end, beam));
        player.PrintToChat($" \x04[LazerTrap]\x01 Trap #{_traps.Count} created. Run css_lazertrap_save to persist it for this map.");
    }

    private void Undo(CCSPlayerController? player)
    {
        if (!IsAdmin(player)) return;
        if (_traps.Count == 0)
        {
            player?.PrintToChat(" \x04[LazerTrap]\x01 No traps to undo.");
            return;
        }
        var last = _traps[^1];
        last.Beam?.Remove();
        _traps.RemoveAt(_traps.Count - 1);
        player?.PrintToChat($" \x04[LazerTrap]\x01 Removed last trap ({_traps.Count} left).");
    }

    private void ClearAll(CCSPlayerController? player)
    {
        if (!IsAdmin(player)) return;
        ClearBeams();
        player?.PrintToChat(" \x04[LazerTrap]\x01 All traps cleared (not yet saved).");
    }

    private void List(CCSPlayerController? player)
    {
        player?.PrintToChat($" \x04[LazerTrap]\x01 {_traps.Count} trap(s) active on {Server.MapName}.");
    }

    // ---------- player-placed temporary lasers (no admin flag needed) ----------

    private void PlacePlayerLaser(CCSPlayerController? player)
    {
        try
        {
            PlacePlayerLaserInternal(player);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[LazerTrap] css_laser error: {ex}");
            player?.PrintToChat(" \x02[Laser]\x01 Something went wrong placing the laser.");
        }
    }

    private void PlacePlayerLaserInternal(CCSPlayerController? player)
    {
        if (!Config.PlayerPlaceEnabled)
            return;

        var pawn = player?.PlayerPawn.Value;
        if (player == null || pawn == null || pawn.AbsOrigin == null
            || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            return;

        if (Config.PlayerPlaceHumansOnly && player.TeamNum == Config.ZombieTeam)
        {
            player.PrintToChat(" \x02[Laser]\x01 Zombies can't place lasers.");
            return;
        }

        float now = Server.CurrentTime;
        if (_placeCooldown.TryGetValue(player.Slot, out var last) && now - last < Config.PlayerPlaceCooldown)
        {
            float left = Config.PlayerPlaceCooldown - (now - last);
            player.PrintToChat($" \x02[Laser]\x01 On cooldown: {left:0.0}s left.");
            return;
        }

        var origin = pawn.AbsOrigin;
        var start = new System.Numerics.Vector3(origin.X, origin.Y, origin.Z + 40f);

        // aim-at-wall placement: laser stretches from the player to wherever their crosshair hits.
        // Falls back to a fixed-length straight line if the trace comes back empty for any reason.
        var end = TraceWallPoint(player) ?? (start + ForwardFlat(pawn) * Config.PlayerPlaceLength);

        var startV = new Vector(start.X, start.Y, start.Z);
        var endV = new Vector(end.X, end.Y, end.Z);
        var beam = CreateBeam(startV, endV);

        _tempTraps.Add((startV, endV, beam, now + Config.PlayerPlaceDuration));
        _placeCooldown[player.Slot] = now;

        player.PrintToChat($" \x04[Laser]\x01 Placed — lasts {Config.PlayerPlaceDuration:0}s.");
    }

    private System.Numerics.Vector3? TraceWallPoint(CCSPlayerController player)
    {
        try
        {
            CGameTrace? trace = player.GetGameTraceByEyePosition(TraceMask.MaskShot, Contents.Solid, player);
            if (trace == null)
                return null;

            var hit = trace.Value.EndPos;
            if (hit == null)
                return null;

            return new System.Numerics.Vector3(hit.X, hit.Y, hit.Z);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[LazerTrap] TraceWallPoint error: {ex}");
            return null;
        }
    }

    private static System.Numerics.Vector3 ForwardFlat(CBasePlayerPawn pawn)
    {
        float ry = pawn.EyeAngles.Y * MathF.PI / 180f;
        return new System.Numerics.Vector3(MathF.Cos(ry), MathF.Sin(ry), 0);
    }

    private void Save(CCSPlayerController? player)
    {
        if (!IsAdmin(player)) return;
        try
        {
            var map = Server.MapName;
            var dict = LoadAllTraps();
            dict[map] = _traps.Select(t => new TrapDef
            {
                SX = t.Start.X,
                SY = t.Start.Y,
                SZ = t.Start.Z,
                EX = t.End.X,
                EY = t.End.Y,
                EZ = t.End.Z
            }).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(TrapsFile)!);
            File.WriteAllText(TrapsFile, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            player?.PrintToChat($" \x04[LazerTrap]\x01 Saved {_traps.Count} trap(s) for {map}.");
        }
        catch (Exception ex)
        {
            player?.PrintToChat($" \x02[LazerTrap]\x01 Save failed: {ex.Message}");
        }
    }

    private void LoadTrapsForCurrentMap()
    {
        ClearBeams();
        var dict = LoadAllTraps();
        if (!dict.TryGetValue(Server.MapName, out var defs))
            return;

        foreach (var d in defs)
        {
            var start = new Vector(d.SX, d.SY, d.SZ);
            var end = new Vector(d.EX, d.EY, d.EZ);
            var beam = CreateBeam(start, end);
            _traps.Add((start, end, beam));
        }
    }

    private Dictionary<string, List<TrapDef>> LoadAllTraps()
    {
        try
        {
            if (File.Exists(TrapsFile))
                return JsonSerializer.Deserialize<Dictionary<string, List<TrapDef>>>(File.ReadAllText(TrapsFile))
                       ?? new Dictionary<string, List<TrapDef>>();
        }
        catch
        {
            // corrupt/missing file — start fresh instead of crashing plugin load
        }
        return new Dictionary<string, List<TrapDef>>();
    }

    // ---------- damage / knockback loop ----------

    private void OnTick()
    {
        try
        {
            OnTickInternal();
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[LazerTrap] OnTick error: {ex}");
        }
    }

    private void OnTickInternal()
    {
        float now = Server.CurrentTime;

        if (_tempTraps.Count > 0)
        {
            for (int i = _tempTraps.Count - 1; i >= 0; i--)
            {
                if (now >= _tempTraps[i].ExpireAt)
                {
                    _tempTraps[i].Beam?.Remove();
                    _tempTraps.RemoveAt(i);
                }
            }
        }

        if (_traps.Count == 0 && _tempTraps.Count == 0)
            return;

        for (int slot = 0; slot < 64; slot++)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            var pawn = player?.PlayerPawn.Value;
            if (player == null || pawn == null || !pawn.IsValid || pawn.AbsOrigin == null
                || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
                continue;

            bool isZombie = player.TeamNum == Config.ZombieTeam;
            if (!isZombie && !Config.HurtHumans)
                continue;

            if (_lastHit.TryGetValue(slot, out var last) && now - last < Config.DamageInterval)
                continue;

            var pos = new System.Numerics.Vector3(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + 40f);
            bool hit = false;

            foreach (var trap in _traps)
            {
                var s = new System.Numerics.Vector3(trap.Start.X, trap.Start.Y, trap.Start.Z);
                var e = new System.Numerics.Vector3(trap.End.X, trap.End.Y, trap.End.Z);
                var closest = ClosestPointOnSegment(s, e, pos);
                if (System.Numerics.Vector3.Distance(closest, pos) > Config.HitRadius)
                    continue;

                _lastHit[slot] = now;
                ApplyHit(player, pawn, pos, closest);
                hit = true;
                break;
            }

            if (hit) continue;

            foreach (var trap in _tempTraps)
            {
                var s = new System.Numerics.Vector3(trap.Start.X, trap.Start.Y, trap.Start.Z);
                var e = new System.Numerics.Vector3(trap.End.X, trap.End.Y, trap.End.Z);
                var closest = ClosestPointOnSegment(s, e, pos);
                if (System.Numerics.Vector3.Distance(closest, pos) > Config.HitRadius)
                    continue;

                _lastHit[slot] = now;
                ApplyHit(player, pawn, pos, closest);
                break;
            }
        }
    }

    private void ApplyHit(CCSPlayerController player, CBasePlayerPawn pawn, System.Numerics.Vector3 pos, System.Numerics.Vector3 closest)
    {
        int newHealth = pawn.Health - (int)Config.Damage;
        pawn.Health = Math.Max(newHealth, 0);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");

        var away = pos - closest;
        if (away.LengthSquared() < 0.01f)
            away = new System.Numerics.Vector3(0, 0, 1);
        away = System.Numerics.Vector3.Normalize(away);

        var push = away * Config.KnockbackForce;
        push.Z += Config.KnockbackForce * 0.35f; // small upward pop so the knockback isn't purely horizontal

        pawn.Teleport(null, null, new Vector(push.X, push.Y, push.Z));

        player.ExecuteClientCommand($"play {Config.HurtSound}");

        if (pawn.Health <= 0)
        {
            pawn.CommitSuicide(false, true);
            return;
        }

        StartBurning(player.Slot);
    }

    private void StartBurning(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player == null || pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            return;

        pawn.Render = Color.FromArgb(255, 255, 90, 20);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
        player.ExecuteClientCommand($"play {Config.BurnSound}");

        BurnTick(slot, Config.BurnTicks);
    }

    private void BurnTick(int slot, int ticksLeft)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        var pawn = player?.PlayerPawn.Value;
        if (player == null || pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            return;

        if (ticksLeft <= 0)
        {
            pawn.Render = Color.FromArgb(255, 255, 255, 255);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
            return;
        }

        int hp = pawn.Health - Config.BurnDamagePerTick;
        pawn.Health = Math.Max(hp, 0);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");

        if (pawn.Health <= 0)
        {
            pawn.Render = Color.FromArgb(255, 255, 255, 255);
            Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
            pawn.CommitSuicide(false, true);
            return;
        }

        AddTimer(Config.BurnTickInterval, () => BurnTick(slot, ticksLeft - 1));
    }

    // ---------- helpers ----------

    private bool IsAdmin(CCSPlayerController? player)
    {
        if (player == null) return true; // server console
        return AdminManager.PlayerHasPermissions(player, Config.AdminFlag);
    }

    private static System.Numerics.Vector3 ClosestPointOnSegment(System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 p)
    {
        var ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 0.0001f) return a;
        float t = System.Numerics.Vector3.Dot(p - a, ab) / lenSq;
        t = Math.Clamp(t, 0f, 1f);
        return a + ab * t;
    }

    private CEnvBeam? CreateBeam(Vector start, Vector end)
    {
        var beam = Utilities.CreateEntityByName<CEnvBeam>("env_beam");
        if (beam == null || !beam.IsValid)
            return null;

        beam.DispatchSpawn();
        beam.AcceptInput("TurnOn");
        // NOTE: beam.SetModel(...) is intentionally NOT called — it crashed the server (fatal
        // engine error, not a catchable .NET exception). The beam still renders fine without it.
        beam.Width = Config.BeamWidth;
        Utilities.SetStateChanged(beam, "CBeam", "m_fWidth");
        beam.Render = _beamColor;
        Utilities.SetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
        beam.Teleport(start, new QAngle(), new Vector());
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");
        return beam;
    }

    private void ClearBeams()
    {
        foreach (var t in _traps)
            t.Beam?.Remove();
        _traps.Clear();

        foreach (var t in _tempTraps)
            t.Beam?.Remove();
        _tempTraps.Clear();

        _lastHit.Clear();
    }

    private static Color ParseColor(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3
            && int.TryParse(parts[0], out var r)
            && int.TryParse(parts[1], out var g)
            && int.TryParse(parts[2], out var b))
            return Color.FromArgb(255, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        return Color.Red;
    }
}
