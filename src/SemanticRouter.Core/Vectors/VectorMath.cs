using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SemanticRouter;

/// <summary>
/// Allocation-free, SIMD-accelerated vector primitives for embedding comparison.
/// </summary>
/// <remarks>
/// Uses <see cref="Vector{T}"/>, which the JIT lowers to the widest hardware vector available
/// (SSE/AVX2/AVX-512 on x64, AdvSimd on Arm64), with a scalar tail loop for the remainder.
/// No external packages are required.
/// </remarks>
public static class VectorMath
{
    /// <summary>Computes the dot product of two equal-length vectors.</summary>
    /// <exception cref="ArgumentException">The vectors differ in length.</exception>
    public static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        ThrowIfLengthMismatch(x, y);

        float sum = 0f;
        int i = 0;

        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            ReadOnlySpan<Vector<float>> vx = MemoryMarshal.Cast<float, Vector<float>>(x);
            ReadOnlySpan<Vector<float>> vy = MemoryMarshal.Cast<float, Vector<float>>(y);
            Vector<float> acc = Vector<float>.Zero;

            for (int k = 0; k < vx.Length; k++)
            {
                acc += vx[k] * vy[k];
            }

            sum = Vector.Sum(acc);
            i = vx.Length * Vector<float>.Count;
        }

        for (; i < x.Length; i++)
        {
            sum += x[i] * y[i];
        }

        return sum;
    }

    /// <summary>Computes the Euclidean (L2) norm of a vector.</summary>
    public static float Norm(ReadOnlySpan<float> x) => MathF.Sqrt(Dot(x, x));

    /// <summary>
    /// Computes the cosine similarity of two equal-length vectors in a single fused pass.
    /// Returns 0 when either vector has zero magnitude.
    /// </summary>
    /// <returns>A value in the range −1..1, where 1 means identical direction.</returns>
    /// <exception cref="ArgumentException">The vectors differ in length.</exception>
    public static float CosineSimilarity(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        ThrowIfLengthMismatch(x, y);

        float dot = 0f, xx = 0f, yy = 0f;
        int i = 0;

        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            ReadOnlySpan<Vector<float>> vx = MemoryMarshal.Cast<float, Vector<float>>(x);
            ReadOnlySpan<Vector<float>> vy = MemoryMarshal.Cast<float, Vector<float>>(y);
            Vector<float> accDot = Vector<float>.Zero;
            Vector<float> accXx = Vector<float>.Zero;
            Vector<float> accYy = Vector<float>.Zero;

            for (int k = 0; k < vx.Length; k++)
            {
                Vector<float> a = vx[k];
                Vector<float> b = vy[k];
                accDot += a * b;
                accXx += a * a;
                accYy += b * b;
            }

            dot = Vector.Sum(accDot);
            xx = Vector.Sum(accXx);
            yy = Vector.Sum(accYy);
            i = vx.Length * Vector<float>.Count;
        }

        for (; i < x.Length; i++)
        {
            float a = x[i];
            float b = y[i];
            dot += a * b;
            xx += a * a;
            yy += b * b;
        }

        if (xx <= 0f || yy <= 0f)
        {
            return 0f;
        }

        // Take square roots separately so very large magnitudes cannot overflow the product.
        float similarity = dot / (MathF.Sqrt(xx) * MathF.Sqrt(yy));
        return Math.Clamp(similarity, -1f, 1f);
    }

    /// <summary>
    /// Scales <paramref name="x"/> in place to unit length. A zero vector is left unchanged.
    /// </summary>
    /// <remarks>
    /// Once vectors are normalized, cosine similarity reduces to <see cref="Dot"/>, which is how
    /// route indexes avoid recomputing norms on every comparison.
    /// </remarks>
    public static void Normalize(Span<float> x)
    {
        float norm = Norm(x);
        if (norm <= 0f || !float.IsFinite(norm))
        {
            return;
        }

        float scale = 1f / norm;
        int i = 0;

        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            Span<Vector<float>> vx = MemoryMarshal.Cast<float, Vector<float>>(x);
            for (int k = 0; k < vx.Length; k++)
            {
                vx[k] *= scale;
            }

            i = vx.Length * Vector<float>.Count;
        }

        for (; i < x.Length; i++)
        {
            x[i] *= scale;
        }
    }

    /// <summary>Returns a unit-length copy of <paramref name="x"/>.</summary>
    public static float[] ToNormalized(ReadOnlySpan<float> x)
    {
        float[] copy = x.ToArray();
        Normalize(copy);
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowIfLengthMismatch(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        if (x.Length != y.Length)
        {
            ThrowLengthMismatch(x.Length, y.Length);
        }
    }

    private static void ThrowLengthMismatch(int x, int y) =>
        throw new ArgumentException($"Vectors must have the same dimensionality (got {x} and {y}).");
}
