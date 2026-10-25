using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One wizard eye in a recording (#10350; Wire v20, docs/wire.md "Wizard eye"): when it came on and went off, the mana
/// then, its lightmap, LDR and prop adds while on, how long its fade took to rise to 1 and to fall to 0, and its fade
/// sampled from the cast until it was down (seconds from the cast; #10358).
/// </summary>
internal sealed record RecordedWizardEye(double On, double Off, float Mana, float LightmapAdd, float LightmapAddLdr, float PropAddMax,
    double FadeIn, double FadeOut, bool PropsFollowFade, bool PressesChangedNothing, IReadOnlyList<(double T, float Fade)> Fades)
{
    public override string ToString() =>
        FormattableString.Invariant($"wizard eye {On:0.00}–{Off:0.00} s ({Off - On:0.00} s), {Mana:0.#} mana, lightmap +{LightmapAdd:0.###} (LDR +{LightmapAddLdr:0.###}), props up to +{PropAddMax:0.###}; ")
        + FormattableString.Invariant($"fade in {FadeIn:0.00} s, out {FadeOut:0.00} s, props with the fade: {PropsFollowFade}, the presses meanwhile changed nothing: {PressesChangedNothing}");
}

/// <summary>Reads the wizard eyes from a v20 recording (#10350).</summary>
internal static class WizardEyeCalibration
{
    internal static List<RecordedWizardEye> Measure(string path)
    {
        double interval = .015;
        var frames = new List<(double T, WireWizardEye Eye, float Mana, bool Attack)>();
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                if (f.WizardEye.Valid != 0)
                    frames.Add((f.Tick * interval, f.WizardEye, f.Stats.Mana, f.Input.Valid != 0 && f.Input.Buttons.HasFlag(InputButtons.Attack)));
            }
        var eyes = new List<RecordedWizardEye>();
        for (int k = 1; k < frames.Count; k++)
        {
            if (frames[k].Eye.Enabled == 0 || frames[k - 1].Eye.Enabled != 0) continue;
            int off = frames.FindIndex(k, f => f.Eye.Enabled == 0);
            if (off < 0) continue;
            var on = frames[k];
            var during = frames.GetRange(k, off - k);
            double fadeIn = during.FirstOrDefault(f => f.Eye.Fade >= 1).T - on.T;
            int dark = frames.FindIndex(off, f => f.Eye.Fade <= 0);
            double fadeOut = dark < 0 ? double.NaN : frames[dark].T - frames[off].T;
            bool follows = during.All(f => MathF.Abs(f.Eye.PropsAdd - f.Eye.Fade * f.Eye.PropAddMax) < 1e-4f);
            // Its adds as they stood once it was up; a press while it was on (attack held) changed none of them.
            var up = during.Last();
            bool pressesNothing = during.Where(f => f.Attack).All(f => f.Eye.Enabled == 1 && f.Eye.LightmapAdd == up.Eye.LightmapAdd);
            var fades = frames.GetRange(k, (dark < 0 ? frames.Count : dark + 1) - k).Select(f => (f.T - on.T, f.Eye.Fade)).ToList();
            eyes.Add(new RecordedWizardEye(on.T, frames[off].T, frames[k - 1].Mana - on.Mana, up.Eye.LightmapAdd, up.Eye.LightmapAddLdr, up.Eye.PropAddMax,
                fadeIn, fadeOut, follows, pressesNothing, fades));
        }
        return eyes;
    }
}
