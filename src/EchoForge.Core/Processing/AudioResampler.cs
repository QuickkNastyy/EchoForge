using System.Globalization;

namespace EchoForge.Core.Processing;

/// <summary>
/// A deterministic, band-limited sample-rate converter for building processing derivatives.
///
/// <para>
/// Whisper wants 16 kHz mono. Getting there from 48 kHz by taking every third sample would fold
/// everything above 8 kHz back down into the speech band as aliasing, which sounds like nothing in
/// particular and quietly costs recognition accuracy. So each output sample is a windowed-sinc
/// weighted sum of the input around it, with the cutoff set by whichever rate is lower.
/// </para>
///
/// <para>
/// Every number here is derived by integer arithmetic from the frame index, never accumulated. The
/// phase of output frame <c>j</c> is <c>(j * inRate) mod outRate</c>, computed from <c>j</c> each
/// time, so a three-hour derivative has exactly the same rounding behaviour at the end as at the
/// start. Two runs on two machines produce identical bytes.
/// </para>
///
/// <para>
/// The filter weights are precomputed once per distinct phase. There are only
/// <c>outRate / gcd(inRate, outRate)</c> of them — one for 48 kHz to 16 kHz, 160 for 44.1 kHz —
/// so the cost is a small table rather than a transcendental function per sample.
/// </para>
/// </summary>
public sealed class AudioResampler
{
    /// <summary>
    /// Taps per output sample. Odd so the kernel is symmetric about a sample position; 63 is
    /// enough for a stopband deep below the noise floor of a meeting microphone, and cheap
    /// because the cost is per output sample rather than per input sample.
    /// </summary>
    public const int Taps = 63;

    private const int Half = Taps / 2;

    /// <summary>
    /// Cutoff as a fraction of the lower Nyquist. Slightly under 1 so the transition band lands
    /// below the point where aliasing would fold into speech.
    /// </summary>
    private const double CutoffFraction = 0.90;

    private readonly int _inputRate;
    private readonly int _outputRate;
    private readonly int _phases;
    private readonly int _phaseStep;
    private readonly double[] _weights;

    public AudioResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputRate), inputRate, "sample rate must be positive");
        }

        if (outputRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputRate), outputRate, "sample rate must be positive");
        }

        _inputRate = inputRate;
        _outputRate = outputRate;

        int divisor = Gcd(inputRate, outputRate);
        _phases = outputRate / divisor;
        _phaseStep = divisor;
        _weights = BuildWeights(inputRate, outputRate, _phases);
    }

    public int InputRate => _inputRate;

    public int OutputRate => _outputRate;

    /// <summary>Input frames needed either side of a position before an output frame can be produced.</summary>
    public static int Reach => Half;

    /// <summary>
    /// The input frame index this output frame is centred on, and the phase within it.
    ///
    /// <para>
    /// Exact: <c>j * inRate</c> is computed in <see cref="long"/> and divided once. Nothing is
    /// carried between calls, so nothing can drift.
    /// </para>
    /// </summary>
    public (long Index, int Phase) Locate(long outputFrame)
    {
        long scaled = outputFrame * _inputRate;
        return (Math.DivRem(scaled, _outputRate, out long remainder), (int)(remainder / _phaseStep));
    }

    /// <summary>
    /// Produces one output sample from mono input.
    ///
    /// <para>
    /// <paramref name="input"/> holds the frames starting at <paramref name="inputOffset"/> in the
    /// same space <see cref="Locate"/> reports. Positions outside it read as silence, which is
    /// correct at the start and end of an epoch and is why callers keep history across chunks.
    /// </para>
    /// </summary>
    public short Sample(ReadOnlySpan<short> input, long inputOffset, long outputFrame)
    {
        (long centre, int phase) = Locate(outputFrame);
        ReadOnlySpan<double> weights = _weights.AsSpan(phase * Taps, Taps);

        double total = 0;
        for (int tap = 0; tap < Taps; tap++)
        {
            long index = centre - Half + tap - inputOffset;
            if (index >= 0 && index < input.Length)
            {
                total += weights[tap] * input[(int)index];
            }
        }

        return Clamp(total);
    }

    /// <summary>
    /// How many output frames a run of input frames turns into, when it starts at the origin.
    ///
    /// <para>
    /// Used only for sizing. Where an output frame actually lands is decided by session time, not
    /// by counting: a derivative that drifted by a frame per chunk would be useless for seeking.
    /// </para>
    /// </summary>
    public long OutputFrameCount(long inputFrames) =>
        inputFrames <= 0 ? 0 : (inputFrames * _outputRate) / _inputRate;

    /// <summary>Mixes interleaved PCM16 down to mono by averaging, deterministically.</summary>
    public static void MixToMono(ReadOnlySpan<short> interleaved, int channels, Span<short> mono)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        if (channels == 1)
        {
            interleaved[..mono.Length].CopyTo(mono);
            return;
        }

        for (int frame = 0; frame < mono.Length; frame++)
        {
            int offset = frame * channels;
            int total = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                total += interleaved[offset + channel];
            }

            // Truncating division, chosen so the result depends only on the inputs and never on
            // the platform's rounding mode. Averaging rather than summing keeps a loud stereo
            // source from clipping the moment it becomes mono.
            mono[frame] = (short)(total / channels);
        }
    }

    private static double[] BuildWeights(int inputRate, int outputRate, int phases)
    {
        double[] weights = new double[phases * Taps];

        // Cutoff is set by the lower of the two rates: downsampling has to remove everything
        // above the new Nyquist, and upsampling must not invent anything above the old one.
        double ratio = Math.Min(1.0, (double)outputRate / inputRate);
        double cutoff = 0.5 * ratio * CutoffFraction;

        for (int phase = 0; phase < phases; phase++)
        {
            double delta = (double)phase / phases;
            double total = 0;
            int origin = phase * Taps;

            for (int tap = 0; tap < Taps; tap++)
            {
                double distance = tap - Half - delta;
                double value = 2 * cutoff * Sinc(2 * cutoff * distance) * Blackman(tap);
                weights[origin + tap] = value;
                total += value;
            }

            // Normalised so the kernel passes a constant through unchanged. Without this the
            // derivative's level would wobble with the phase, which is audible as a hum.
            if (Math.Abs(total) > double.Epsilon)
            {
                for (int tap = 0; tap < Taps; tap++)
                {
                    weights[origin + tap] /= total;
                }
            }
        }

        return weights;
    }

    private static double Blackman(int tap)
    {
        double x = 2 * Math.PI * tap / (Taps - 1);
        return 0.42 - (0.5 * Math.Cos(x)) + (0.08 * Math.Cos(2 * x));
    }

    private static double Sinc(double x) =>
        Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static short Clamp(double value)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded switch
        {
            >= short.MaxValue => short.MaxValue,
            <= short.MinValue => short.MinValue,
            _ => (short)rounded,
        };
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{_inputRate} Hz -> {_outputRate} Hz, {_phases} phases");
}
