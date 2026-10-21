using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Time;
using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace DarkMessiah.Lab.Developer;

/// <summary>
/// The lab's tunables page for the Engine tunables panel (<see cref="TunablesDebugModule"/>): the body and camera from
/// <c>player.json</c>, the sword and the orc from <c>combat.json</c>, and the world rate (live only). Each control
/// applies through its owner (<see cref="PlayerController.Retune"/>, <see cref="LabCombat.Retune"/>,
/// <see cref="WorldTime.SetRate"/>) at once; Save writes the bound file through the Engine content source (which marks
/// it already staged, so no reload follows: the live value is already applied), and a hand edit of the same file
/// arrives as a live content change. Declarations are explicit lists.
/// Provenance: rusty-hotel <c>Interface/Workbench/HotelWorkbench</c> (the page-and-chooser shape); the controls are the lab's.
/// </summary>
internal sealed class LabTunables(IContentSourceService source, PlayerController player, LabCombat combat, WorldTime time)
{
    private delegate PlayerTuning With(PlayerTuning tuning, float value);

    private static readonly (string Id, string Label, float Min, float Max, float Step, string Pointer, Func<PlayerTuning, float> Get, With Set)[] Body =
    [
        ("speed", "Walk speed", 1, 12, .1f, "/speed", t => t.Speed, (t, v) => t with { Speed = v, SprintSpeed = Math.Max(v, t.SprintSpeed) }),
        ("sprint", "Sprint speed", 1, 16, .1f, "/sprintSpeed", t => t.SprintSpeed, (t, v) => t with { SprintSpeed = Math.Max(v, t.Speed) }),
        ("jump", "Jump speed", 1, 14, .1f, "/jumpSpeed", t => t.JumpSpeed, (t, v) => t with { JumpSpeed = v }),
        ("gravity", "Body gravity", 4, 40, .5f, "/gravity", t => t.Gravity, (t, v) => t with { Gravity = v }),
        ("step", "Step height", 0, .8f, .05f, "/maximumStepHeight", t => t.MaximumStepHeight, (t, v) => t with { MaximumStepHeight = v }),
        ("slope", "Max slope °", 10, 80, 1, "/maximumSlopeDegrees", t => t.MaximumSlopeDegrees, (t, v) => t with { MaximumSlopeDegrees = v }),
        ("buffer", "Jump buffer s", 0, .4f, .01f, "/jumpBufferSeconds", t => t.JumpBufferSeconds, (t, v) => t with { JumpBufferSeconds = v }),
        ("coyote", "Coyote time s", 0, .4f, .01f, "/coyoteSeconds", t => t.CoyoteSeconds, (t, v) => t with { CoyoteSeconds = v }),
    ];

    private static readonly (string Id, string Label, float Min, float Max, float Step, string Pointer, Func<PlayerTuning, float> Get, With Set)[] View =
    [
        ("fov", "Field of view °", 50, 110, 1, "/fieldOfViewDegrees", t => t.FieldOfViewDegrees, (t, v) => t with { FieldOfViewDegrees = v }),
        ("sensitivity", "Mouse rad/unit", .0005f, .006f, .0001f, "/pointerRadiansPerUnit", t => t.PointerRadiansPerUnit, (t, v) => t with { PointerRadiansPerUnit = v }),
        ("bob", "Head bob m", 0, .1f, .005f, "/headBobMetres", t => t.HeadBobMetres, (t, v) => t with { HeadBobMetres = v }),
        ("bob-stride", "Bob stride m", .5f, 3, .05f, "/headBobStrideMetres", t => t.HeadBobStrideMetres, (t, v) => t with { HeadBobStrideMetres = v }),
    ];

    private delegate CombatTuning WithCombat(CombatTuning tuning, float value);

    private static readonly (string Id, string Label, float Min, float Max, float Step, string Pointer, Func<CombatTuning, float> Get, WithCombat Set)[] SwordControls =
    [
        ("move", "Move while busy", 0, 1, .05f, "/attackMovementScale", t => t.AttackMovementScale, (t, v) => t with { AttackMovementScale = v }),
        ("kick-range", "Kick range m", .3f, 3, .05f, "/kickRange", t => t.KickRange, (t, v) => t with { KickRange = v }),
        ("kick-push", "Kick push m", 0, 4, .1f, "/kickPush", t => t.KickPush, (t, v) => t with { KickPush = v }),
        // Each move's windup, active (committed) and recovery phases, as multiples of the donor clip's own timing.
        ("fast-windup", "Fast windup ×", .25f, 4, .05f, "/phases/fast/windup", t => t.Phases.Fast.Windup, (t, v) => t with { Phases = t.Phases with { Fast = t.Phases.Fast with { Windup = v } } }),
        ("fast-active", "Fast active ×", .25f, 4, .05f, "/phases/fast/active", t => t.Phases.Fast.Active, (t, v) => t with { Phases = t.Phases with { Fast = t.Phases.Fast with { Active = v } } }),
        ("fast-recovery", "Fast recovery ×", .25f, 4, .05f, "/phases/fast/recovery", t => t.Phases.Fast.Recovery, (t, v) => t with { Phases = t.Phases with { Fast = t.Phases.Fast with { Recovery = v } } }),
        ("power-windup", "Power windup ×", .25f, 4, .05f, "/phases/power/windup", t => t.Phases.Power.Windup, (t, v) => t with { Phases = t.Phases with { Power = t.Phases.Power with { Windup = v } } }),
        ("power-active", "Power active ×", .25f, 4, .05f, "/phases/power/active", t => t.Phases.Power.Active, (t, v) => t with { Phases = t.Phases with { Power = t.Phases.Power with { Active = v } } }),
        ("power-recovery", "Power recovery ×", .25f, 4, .05f, "/phases/power/recovery", t => t.Phases.Power.Recovery, (t, v) => t with { Phases = t.Phases with { Power = t.Phases.Power with { Recovery = v } } }),
        ("kick-windup", "Kick windup ×", .25f, 4, .05f, "/phases/kick/windup", t => t.Phases.Kick.Windup, (t, v) => t with { Phases = t.Phases with { Kick = t.Phases.Kick with { Windup = v } } }),
        ("kick-active", "Kick active ×", .25f, 4, .05f, "/phases/kick/active", t => t.Phases.Kick.Active, (t, v) => t with { Phases = t.Phases with { Kick = t.Phases.Kick with { Active = v } } }),
        ("kick-recovery", "Kick recovery ×", .25f, 4, .05f, "/phases/kick/recovery", t => t.Phases.Kick.Recovery, (t, v) => t with { Phases = t.Phases with { Kick = t.Phases.Kick with { Recovery = v } } }),
        ("health", "Player health", 1, 400, 1, "/playerHealth", t => t.PlayerHealth, (t, v) => t with { PlayerHealth = v }),
    ];

    private static readonly (string Id, string Label, float Min, float Max, float Step, string Pointer, Func<CombatTuning, float> Get, WithCombat Set)[] OrcControls =
    [
        ("walk", "Walk speed units/s", 20, 160, 1, "/orc/walkSpeed", t => t.Orc.WalkSpeed, (t, v) => t with { Orc = t.Orc with { WalkSpeed = v } }),
        ("respawn", "Respawn s", 0, 20, .5f, "/orc/respawnSeconds", t => t.Orc.RespawnSeconds, (t, v) => t with { Orc = t.Orc with { RespawnSeconds = v } }),
    ];

    // The page has no choosers.
    internal TunablesDebugModule Module() => new(source, Page, (chooser, _) => $"Unknown chooser '{chooser}'.");

    private TunablesPage Page()
    {
        PlayerTuning t = player.Tuning;
        Tunable BodyControl((string Id, string Label, float Min, float Max, float Step, string Pointer, Func<PlayerTuning, float> Get, With Set) c) =>
            new(new TunableControl(c.Id, c.Label, TunableKind.Slider, c.Min, c.Max, c.Step, c.Get(t)) { Source = new(PlayerTuning.Path, c.Pointer) },
                v => player.Retune(PlayerTuning.Validate(c.Set(player.Tuning, v))));
        Tunable CombatControl(string group, (string Id, string Label, float Min, float Max, float Step, string Pointer, Func<CombatTuning, float> Get, WithCombat Set) c) =>
            new(new TunableControl($"{group}.{c.Id}", c.Label, TunableKind.Slider, c.Min, c.Max, c.Step, c.Get(combat.Tuning)) { Source = new(CombatTuning.Path, c.Pointer) },
                v => combat.Retune(CombatTuning.Validate(c.Set(combat.Tuning, v))));
        // One slider per skill, 0 to its maximum level, applied through the build's own validation.
        Tunable BuildControl(SkillInfo skill) =>
            new(new TunableControl($"build.{skill.Name}", $"{skill.Name} (max {skill.MaxLevel})", TunableKind.Slider, 0, skill.MaxLevel, 1,
                    combat.BuildFile?.Skills.GetValueOrDefault(skill.Name) ?? 0) { Source = new(BuildFile.Path, $"/skills/{skill.Name}") },
                v =>
                {
                    var file = combat.BuildFile ?? throw new InvalidOperationException("No build loaded.");
                    combat.Rebuild(file with { Skills = new Dictionary<string, int>(file.Skills) { [skill.Name] = (int)MathF.Round(v) } });
                });
        return new TunablesPage("Dark Messiah combat lab",
        [
            new TunableGroup("body", "Body", Body.Select(BodyControl).ToArray()) { File = PlayerTuning.Path },
            new TunableGroup("view", "View", View.Select(BodyControl).ToArray()) { File = PlayerTuning.Path },
            new TunableGroup("sword", "Sword (the lab's; clips, damage and range are Arkane's)", SwordControls.Select(c => CombatControl("sword", c)).ToArray()) { File = CombatTuning.Path },
            new TunableGroup("orc", "Orc", OrcControls.Select(c => CombatControl("orc", c)).ToArray()) { File = CombatTuning.Path },
            new TunableGroup("build", "Build (skill levels from mm_skills_infos.txt; a skill above 0 needs what it requires)",
                combat.Skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal).Select(BuildControl).ToArray()) { File = BuildFile.Path },
            new TunableGroup("world", "World time (not saved)",
            [
                new Tunable(new TunableControl("world.rate", "World rate ×", TunableKind.Slider, .05f, 1, .05f, (float)time.Rate), v => time.SetRate(v)),
                new Tunable(new TunableControl("world.held", "Hold world", TunableKind.Toggle, 0, 1, 1, time.Held ? 1 : 0), v => { if (v > .5f) time.Hold(); else time.Run(); }),
            ]),
        ])
        {
            Notice = "Changes apply at once. Save writes body and view to player.json, the sword and orc to combat.json and the skill levels to build.json; hand edits to those files reload live. World time is not saved.",
        };
    }
}
