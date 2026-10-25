using DarkMessiah.Lab.Donor;

namespace DarkMessiah.Lab.Magic;

/// <summary>
/// The wizard eye (<c>spells.txt</c>'s <c>water_1</c>; #10350, #10358, docs/lab.md "Wizard eye"): no remote view (server.dll's
/// <c>EnterWizardEye</c> is an empty stub) but a brightening of the lighting for <c>effect_duration</c> (30 s), cast as
/// it's put in hand for <c>cost</c> (0). Its adds are <c>Lightmap_Max_Add</c> (0.002; LDR 0.01) on the lightmaps and
/// its fade times <c>Props_Max_Add</c> (0.01) on the props. Measured (<c>l01_a-v20-wizard-eye</c>): the fade rises 0.03
/// every two ticks (1 a second) to 1, and once the 30 s are over falls 0.06 every two ticks (2 a second) to 0.
/// </summary>
internal sealed record WizardEyeRules(float Cost, float Seconds, float LightmapAdd, float LightmapAddLdr, float PropsAdd)
{
    internal const string Spell = "water_1";
    /// <summary>How fast the fade rises while it's on, a second: the recorded 0.03 every two ticks.</summary>
    internal const float FadeIn = 1;
    /// <summary>How fast it falls once it's off, a second: the recorded 0.06 every two ticks.</summary>
    internal const float FadeOut = 2;

    internal static readonly WizardEyeRules Shipped = new(0, 30, .002f, .01f, .01f);

    internal static WizardEyeRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(WizardEyeRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Seconds = Spell("effect_duration") ?? Shipped.Seconds,
            LightmapAdd = Spell("Lightmap_Max_Add") ?? Shipped.LightmapAdd,
            LightmapAddLdr = Spell("Lightmap_Max_Add_LDR") ?? Shipped.LightmapAddLdr,
            PropsAdd = Spell("Props_Max_Add") ?? Shipped.PropsAdd,
        };
    }
}

/// <summary>The player's wizard eye (#10358): on for its seconds, and its fade, which the arena's brightening follows.</summary>
internal sealed class WizardEye(WizardEyeRules rules)
{
    internal WizardEyeRules Rules { get; } = rules;

    /// <summary>Arkane's <c>m_bMMWizardEyeEnable</c>.</summary>
    internal bool On => Left > 0;

    /// <summary>Seconds it has left on (<c>m_g_MM_fWizardEyeDuration</c>); 0 when it's off.</summary>
    internal float Left { get; private set; }

    /// <summary>0 off, rising to 1 while it's on and falling back once it's over.</summary>
    internal float Fade { get; private set; }

    /// <summary>The props' add now: its fade times <see cref="WizardEyeRules.PropsAdd"/>.</summary>
    internal float PropsAdd => Fade * Rules.PropsAdd;

    /// <summary>
    /// The lab's ambient light at full fade, white, over the Engine's neutral rig. The donor's lightmap and props adds
    /// are its client's own shading (not on the Wire); in daylight (<c>wizard-eye-look</c>, the game's own screenshots in
    /// the l01_a courtyard) they wash 98% of the view out to white from 2 s in and through its 30 s, the props as well
    /// as the world, and the view is as it was once the fade is down. At 40 the lab's arena is white (headless
    /// captures) and the crate nearly (its mean 91 to 242 of 255); the orc's darker skin keeps some of its texture
    /// (8 left 10% of the arena unwashed and the crate at 177; 16 left the crate at 216).
    /// </summary>
    internal const float FullLight = 40;

    /// <summary>The lab's ambient light now: its fade times <see cref="FullLight"/>.</summary>
    internal float Light => Fade * FullLight;

    /// <summary>On for its full seconds. Cast again while it's on (not recorded), it starts its seconds over.</summary>
    internal void Cast() => Left = Rules.Seconds;

    internal void Step(float seconds)
    {
        if (seconds <= 0) return;
        // Less than half a step from the end, it's there: steps of 1/60 s don't sum exactly in floats.
        Left -= seconds;
        if (Left < seconds / 2) Left = 0;
        if (On) Fade = Fade + WizardEyeRules.FadeIn * seconds > 1 - WizardEyeRules.FadeIn * seconds / 2 ? 1 : Fade + WizardEyeRules.FadeIn * seconds;
        else Fade = Fade - WizardEyeRules.FadeOut * seconds < WizardEyeRules.FadeOut * seconds / 2 ? 0 : Fade - WizardEyeRules.FadeOut * seconds;
    }

    internal void Reset() => Left = Fade = 0;
}
