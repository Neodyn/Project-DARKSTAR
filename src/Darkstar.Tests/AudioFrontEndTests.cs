using static Darkstar.Tests.Test;

namespace Darkstar.Tests;

/// <summary>The audio path that feeds the wake word detector.</summary>
internal static class AudioFrontEndTests
{
    public static void Run()
    {
        // Measures what the new audio front end actually does, rather than taking the design on faith:
        // real sine waves in, amplitude of the fold-over product out, both filter paths side by side.

        const int Fs = 48000;
        const int FsOut = 16000;



        // --- helpers -------------------------------------------------------------------------------

        static byte[] Sine(double freqHz, int sampleRate, int samples, double amplitude = 8000)
        {
            var pcm = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                var v = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * freqHz * i / sampleRate));
                BitConverter.TryWriteBytes(pcm.AsSpan(i * 2, 2), v);
            }
            return pcm;
        }

        // Goertzel: amplitude of one frequency component, without pulling in an FFT library.
        static double Amplitude(byte[] pcm16, double freqHz, int sampleRate, int skipSamples = 0)
        {
            int total = pcm16.Length / 2;
            int n = total - skipSamples;
            if (n <= 0) return 0;

            double w = 2 * Math.PI * freqHz / sampleRate;
            double cosW = Math.Cos(w), coeff = 2 * cosW;
            double s1 = 0, s2 = 0;

            for (int i = skipSamples; i < total; i++)
            {
                double s0 = BitConverter.ToInt16(pcm16, i * 2) + coeff * s1 - s2;
                s2 = s1; s1 = s0;
            }

            double real = s1 - s2 * cosW;
            double imag = s2 * Math.Sin(w);
            return 2 * Math.Sqrt(real * real + imag * imag) / n;
        }

        static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-12));

        // Runs a whole signal through a fresh filter in 20 ms frames, the way the live path does.
        static byte[] RunNew(byte[] pcm48k, int frameSamples = 960)
        {
            var filter = new DecimatingLowPass();
            using var outBuf = new MemoryStream();
            for (int offset = 0; offset < pcm48k.Length; offset += frameSamples * 2)
            {
                int len = Math.Min(frameSamples * 2, pcm48k.Length - offset);
                var chunk = pcm48k.AsSpan(offset, len).ToArray();
                var res = filter.Process(chunk);
                outBuf.Write(res, 0, res.Length);
            }
            return outBuf.ToArray();
        }

        static byte[] RunOld(byte[] pcm48k, int frameSamples = 960)
        {
            using var outBuf = new MemoryStream();
            for (int offset = 0; offset < pcm48k.Length; offset += frameSamples * 2)
            {
                int len = Math.Min(frameSamples * 2, pcm48k.Length - offset);
                var res = DecimatingLowPass.AverageDecimate(pcm48k.AsSpan(offset, len).ToArray());
                outBuf.Write(res, 0, res.Length);
            }
            return outBuf.ToArray();
        }

        // --- filter design -------------------------------------------------------------------------

        Console.WriteLine("Filter design");

        var taps = DecimatingLowPass.DesignLowPass(
            DecimatingLowPass.DefaultTapCount, DecimatingLowPass.DefaultCutoffHz, Fs);

        Check("tap count is odd (symmetric, linear phase)", taps.Length % 2 == 1, $"{taps.Length} taps");

        double tapSum = taps.Sum();
        Check("unity gain at DC (the filter doesn't change the level)",
            Math.Abs(tapSum - 1.0) < 1e-5, $"sum = {tapSum:F8}");

        bool symmetric = true;
        for (int i = 0; i < taps.Length / 2; i++)
            if (Math.Abs(taps[i] - taps[taps.Length - 1 - i]) > 1e-7) symmetric = false;
        Check("coefficients are symmetric", symmetric);

        Check("an even tap count is corrected to odd",
            DecimatingLowPass.DesignLowPass(160, 7200, Fs).Length == 161);

        var threw = false;
        try { DecimatingLowPass.DesignLowPass(161, 30000, Fs); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check("a cutoff above Nyquist is refused", threw);

        // --- what it does to speech ----------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Passband: the frequencies speech actually lives in must survive");

        // Skip the filter's group delay plus settling before measuring.
        int settle = taps.Length;

        foreach (var f in new[] { 100.0, 300, 500, 1000, 2000, 3000, 4000, 5000, 6000 })
        {
            var input = Sine(f, Fs, Fs); // one second
            var outNew = RunNew(input);
            double gain = Amplitude(outNew, f, FsOut, settle) / 8000.0;
            // Everything up to 6 kHz should pass essentially untouched (within 1 dB).
            Check($"{f,5:0} Hz passes through", Math.Abs(Db(gain)) < 1.0, $"{Db(gain),6:F2} dB");
        }

        Console.WriteLine();
        Console.WriteLine("Stopband: what folds down must be pushed far enough down first");

        // Content above 8 kHz aliases to |f - 16000| after decimation by 3. That is the whole problem.
        Console.WriteLine("  freq in   aliases to     old path      new path     improvement");
        var improvements = new List<double>();

        foreach (var f in new[] { 9000.0, 10000, 11000, 12000, 13000, 14000, 15000 })
        {
            double aliasFreq = Math.Abs(f - FsOut);
            var input = Sine(f, Fs, Fs);

            double oldAlias = Amplitude(RunOld(input), aliasFreq, FsOut, settle) / 8000.0;
            double newAlias = Amplitude(RunNew(input), aliasFreq, FsOut, settle) / 8000.0;

            double improvement = Db(oldAlias) - Db(newAlias);
            improvements.Add(improvement);

            Console.WriteLine($"  {f,7:0} Hz   {aliasFreq,6:0} Hz   {Db(oldAlias),8:F1} dB   {Db(newAlias),8:F1} dB   {improvement,8:F1} dB better");
        }

        Check("every fold-over product is pushed down by at least 30 dB more than before",
            improvements.All(i => i >= 30), $"worst case {improvements.Min():F1} dB");

        // The band that matters most: 10-12 kHz folds onto 4-6 kHz, where consonants are told apart.
        var worstInConsonantBand = new[] { 10000.0, 11000, 12000 }
            .Select(f => Db(Amplitude(RunNew(Sine(f, Fs, Fs)), Math.Abs(f - FsOut), FsOut, settle) / 8000.0))
            .Max();
        Check("nothing folds into the 4-6 kHz consonant band above -45 dB",
            worstInConsonantBand < -45, $"loudest is {worstInConsonantBand:F1} dB");

        // --- state handling ------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("State: the filter must not restart at every 20 ms frame boundary");

        var speechLike = Sine(440, Fs, Fs / 2);

        // Same audio, different chunk sizes: the output has to be identical, which it can only be if
        // the delay line and the decimation phase both survive across calls.
        var inOneGo = RunNew(speechLike, Fs / 2);
        var in20msFrames = RunNew(speechLike, 960);
        var inRaggedFrames = RunNew(speechLike, 1000); // not a multiple of 3: phase must be tracked

        Check("20 ms frames give the same result as one big block",
            inOneGo.SequenceEqual(in20msFrames),
            $"{inOneGo.Length / 2} vs {in20msFrames.Length / 2} samples");

        Check("frames whose length isn't a multiple of 3 give the same result too",
            inOneGo.SequenceEqual(inRaggedFrames),
            $"{inOneGo.Length / 2} vs {inRaggedFrames.Length / 2} samples");

        Check("output length is input length / 3 (no samples lost or invented)",
            Math.Abs(in20msFrames.Length / 2 - speechLike.Length / 2 / 3) <= 1,
            $"{in20msFrames.Length / 2} out of {speechLike.Length / 2} in");

        // The old path silently dropped up to two samples per frame: at 50 frames a second that is a
        // slow drift between what was said and what the recognizer hears.
        var oldFrames = RunOld(speechLike, 1000);
        Check("the old path did lose samples on ragged frames (regression guard)",
            oldFrames.Length / 2 < inRaggedFrames.Length / 2,
            $"old {oldFrames.Length / 2}, new {inRaggedFrames.Length / 2}");

        Check("an empty chunk is handled", new DecimatingLowPass().Process(Array.Empty<byte>()).Length == 0);

        var filterForReset = new DecimatingLowPass();
        filterForReset.Process(Sine(1000, Fs, 4800));
        filterForReset.Reset();
        var afterReset = filterForReset.Process(speechLike);
        Check("Reset() puts it back to a known state", afterReset.SequenceEqual(RunNew(speechLike, speechLike.Length / 2)));

        // --- clipping ------------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("Loud audio");

        var loud = Sine(1000, Fs, Fs / 4, amplitude: 32767);
        var loudOut = RunNew(loud);
        int peak = 0;
        bool signFlipped = false;
        for (int i = 1; i < loudOut.Length / 2; i++)
        {
            int s = BitConverter.ToInt16(loudOut, i * 2);
            peak = Math.Max(peak, Math.Abs(s));
            // A wrapped sample shows up as a huge jump to the opposite sign between neighbours.
            int prev = BitConverter.ToInt16(loudOut, (i - 1) * 2);
            if (Math.Abs(s - prev) > 40000) signFlipped = true;
        }
        Check("a full-scale signal doesn't wrap around into crackle", !signFlipped && peak > 30000, $"peak {peak}");

        // --- automatic gain ------------------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("SpeechAutoGain (opt-in)");

        var agc = new SpeechAutoGain();
        Check("starts at unity gain", Math.Abs(agc.CurrentGain - 1.0f) < 1e-6);

        // A quiet pilot: many frames of low-level speech should be brought up, but only so far.
        for (int i = 0; i < 200; i++)
            agc.Process(Sine(500, Fs, 960, amplitude: 800));
        Check("a quiet transmission is amplified", agc.CurrentGain > 2.0f, $"gain {agc.CurrentGain:F2}");
        Check("but never beyond the ceiling", agc.CurrentGain <= SpeechAutoGain.MaxGain + 1e-6, $"gain {agc.CurrentGain:F2}");

        // Silence must not wind the gain up - that is how an AGC invents wake words.
        var quiet = new SpeechAutoGain();
        for (int i = 0; i < 500; i++)
            quiet.Process(Sine(500, Fs, 960, amplitude: 50)); // below the silence floor
        Check("room noise does not wind the gain up", Math.Abs(quiet.CurrentGain - 1.0f) < 1e-6, $"gain {quiet.CurrentGain:F2}");

        // A loud pilot must not be attenuated.
        var loudAgc = new SpeechAutoGain();
        for (int i = 0; i < 200; i++)
            loudAgc.Process(Sine(500, Fs, 960, amplitude: 20000));
        Check("a loud transmission is left alone, not turned down", Math.Abs(loudAgc.CurrentGain - 1.0f) < 1e-6, $"gain {loudAgc.CurrentGain:F2}");

        var clipTest = new SpeechAutoGain();
        for (int i = 0; i < 300; i++) clipTest.Process(Sine(500, Fs, 960, amplitude: 700));
        var loudFrame = Sine(500, Fs, 960, amplitude: 30000);
        clipTest.Process(loudFrame);
        bool wrapped = false;
        for (int i = 0; i < 960; i++)
        {
            // A wrap-around shows up as a sign flip against the expected sine.
            double expected = Math.Sin(2 * Math.PI * 500 * i / Fs);
            short actual = BitConverter.ToInt16(loudFrame, i * 2);
            if (Math.Abs(expected) > 0.5 && Math.Sign(actual) != Math.Sign(expected)) wrapped = true;
        }
        Check("amplifying an already-loud frame clamps instead of wrapping", !wrapped);


        // ============================================================================================
        // WAV reading - the test runner has to cope with files recorded elsewhere, not just ours.
        // ============================================================================================

        Console.WriteLine();
        Console.WriteLine("WavUtils round trip");

        var tmp = Path.Combine(Path.GetTempPath(), "wavtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var original = Sine(1000, Fs, 4800);
            var file = Path.Combine(tmp, "mono48k.wav");
            File.WriteAllBytes(file, WavUtils.WrapPcm16AsWav(original, Fs));

            var read = WavUtils.ReadPcm16(file);
            Check("what we wrote is what we read back", read.Pcm16.SequenceEqual(original));
            Check("sample rate survives", read.SampleRate == Fs, $"{read.SampleRate} Hz");
            Check("channel count survives", read.Channels == 1);
            Check("duration is right", Math.Abs(read.DurationSeconds - 0.1) < 1e-6, $"{read.DurationSeconds:F3}s");

            // A file with an extra chunk before the audio, as Audacity and others write.
            var withList = new List<byte>();
            var body = new List<byte>();
            void Ascii(List<byte> t, string s) => t.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
            Ascii(body, "WAVE");
            Ascii(body, "LIST"); body.AddRange(BitConverter.GetBytes(10)); body.AddRange(new byte[10]);
            Ascii(body, "fmt "); body.AddRange(BitConverter.GetBytes(16));
            body.AddRange(BitConverter.GetBytes((short)1));
            body.AddRange(BitConverter.GetBytes((short)1));
            body.AddRange(BitConverter.GetBytes(Fs));
            body.AddRange(BitConverter.GetBytes(Fs * 2));
            body.AddRange(BitConverter.GetBytes((short)2));
            body.AddRange(BitConverter.GetBytes((short)16));
            Ascii(body, "data"); body.AddRange(BitConverter.GetBytes(original.Length)); body.AddRange(original);
            Ascii(withList, "RIFF"); withList.AddRange(BitConverter.GetBytes(body.Count)); withList.AddRange(body);

            var listFile = Path.Combine(tmp, "withlist.wav");
            File.WriteAllBytes(listFile, withList.ToArray());
            var readList = WavUtils.ReadPcm16(listFile);
            Check("a LIST chunk before the audio is skipped, not misread",
                readList.Pcm16.SequenceEqual(original) && readList.SampleRate == Fs);

            // Stereo -> mono.
            var stereo = new byte[original.Length * 2];
            for (int i = 0; i < original.Length / 2; i++)
            {
                short s = BitConverter.ToInt16(original, i * 2);
                BitConverter.TryWriteBytes(stereo.AsSpan(i * 4, 2), s);
                BitConverter.TryWriteBytes(stereo.AsSpan(i * 4 + 2, 2), s);
            }
            var mono = WavUtils.ToMono(stereo, 2);
            Check("stereo is averaged down to mono", mono.SequenceEqual(original));
            Check("mono is passed through untouched", ReferenceEquals(WavUtils.ToMono(original, 1), original));

            // Things that must be refused rather than silently producing garbage audio.
            var notWav = Path.Combine(tmp, "notawav.wav");
            File.WriteAllText(notWav, "this is not a wav file at all, not even close");
            bool refused = false;
            try { WavUtils.ReadPcm16(notWav); } catch (InvalidDataException) { refused = true; }
            Check("a non-WAV file is refused", refused);

            var noData = Path.Combine(tmp, "nodata.wav");
            var hdr = new List<byte>();
            var b2 = new List<byte>();
            Ascii(b2, "WAVE");
            Ascii(b2, "fmt "); b2.AddRange(BitConverter.GetBytes(16));
            b2.AddRange(BitConverter.GetBytes((short)1)); b2.AddRange(BitConverter.GetBytes((short)1));
            b2.AddRange(BitConverter.GetBytes(Fs)); b2.AddRange(BitConverter.GetBytes(Fs * 2));
            b2.AddRange(BitConverter.GetBytes((short)2)); b2.AddRange(BitConverter.GetBytes((short)16));
            Ascii(hdr, "RIFF"); hdr.AddRange(BitConverter.GetBytes(b2.Count)); hdr.AddRange(b2);
            File.WriteAllBytes(noData, hdr.ToArray());
            refused = false;
            try { WavUtils.ReadPcm16(noData); } catch (InvalidDataException) { refused = true; }
            Check("a WAV without audio data is refused", refused);

            // 8-bit audio: refused with a message rather than played back as noise.
            var eightBit = Path.Combine(tmp, "8bit.wav");
            var b3 = new List<byte>();
            var h3 = new List<byte>();
            Ascii(b3, "WAVE");
            Ascii(b3, "fmt "); b3.AddRange(BitConverter.GetBytes(16));
            b3.AddRange(BitConverter.GetBytes((short)1)); b3.AddRange(BitConverter.GetBytes((short)1));
            b3.AddRange(BitConverter.GetBytes(Fs)); b3.AddRange(BitConverter.GetBytes(Fs));
            b3.AddRange(BitConverter.GetBytes((short)1)); b3.AddRange(BitConverter.GetBytes((short)8));
            Ascii(b3, "data"); b3.AddRange(BitConverter.GetBytes(100)); b3.AddRange(new byte[100]);
            Ascii(h3, "RIFF"); h3.AddRange(BitConverter.GetBytes(b3.Count)); h3.AddRange(b3);
            File.WriteAllBytes(eightBit, h3.ToArray());
            var message = "";
            try { WavUtils.ReadPcm16(eightBit); } catch (InvalidDataException ex) { message = ex.Message; }
            Check("8-bit audio is refused with a message that says why", message.Contains("16-bit"), message);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
