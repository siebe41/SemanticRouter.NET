namespace SemanticRouter.Tests.Core;

public class VectorMathTests
{
    [Fact]
    public void CosineSimilarity_of_identical_vectors_is_one()
    {
        float[] v = [1f, 2f, 3f, 4f];

        VectorMath.CosineSimilarity(v, v).Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public void CosineSimilarity_is_scale_invariant()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [10f, 20f, 30f];

        VectorMath.CosineSimilarity(a, b).Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public void CosineSimilarity_of_orthogonal_vectors_is_zero()
    {
        VectorMath.CosineSimilarity([1f, 0f], [0f, 1f]).Should().BeApproximately(0f, 1e-6f);
    }

    [Fact]
    public void CosineSimilarity_of_opposite_vectors_is_minus_one()
    {
        VectorMath.CosineSimilarity([1f, -2f, 3f], [-1f, 2f, -3f]).Should().BeApproximately(-1f, 1e-6f);
    }

    [Fact]
    public void CosineSimilarity_with_a_zero_vector_is_zero_rather_than_NaN()
    {
        VectorMath.CosineSimilarity([0f, 0f, 0f], [1f, 2f, 3f]).Should().Be(0f);
    }

    [Fact]
    public void Mismatched_lengths_throw()
    {
        Action act = () => VectorMath.CosineSimilarity([1f, 2f], [1f, 2f, 3f]);

        act.Should().Throw<ArgumentException>().WithMessage("*same dimensionality*");
    }

    [Fact]
    public void Empty_vectors_have_zero_similarity()
    {
        VectorMath.CosineSimilarity([], []).Should().Be(0f);
        VectorMath.Dot([], []).Should().Be(0f);
    }

    // Lengths chosen to straddle every SIMD width (4/8/16 floats) and exercise the scalar tail loop.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(384)]
    [InlineData(1536)]
    [InlineData(3073)]
    public void Simd_results_match_a_scalar_reference(int length)
    {
        var random = new Random(length);
        float[] a = Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        float[] b = Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();

        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < length; i++)
        {
            dot += (double)a[i] * b[i];
            aa += (double)a[i] * a[i];
            bb += (double)b[i] * b[i];
        }

        VectorMath.Dot(a, b).Should().BeApproximately((float)dot, 1e-3f);
        VectorMath.CosineSimilarity(a, b).Should().BeApproximately((float)(dot / (Math.Sqrt(aa) * Math.Sqrt(bb))), 1e-4f);
    }

    [Fact]
    public void Normalize_produces_a_unit_vector_in_place()
    {
        float[] v = [3f, 4f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];

        VectorMath.Normalize(v);

        VectorMath.Norm(v).Should().BeApproximately(1f, 1e-6f);
        v[0].Should().BeApproximately(0.6f, 1e-6f);
        v[1].Should().BeApproximately(0.8f, 1e-6f);
    }

    [Fact]
    public void Normalize_leaves_a_zero_vector_unchanged()
    {
        float[] v = new float[16];

        VectorMath.Normalize(v);

        v.Should().OnlyContain(x => x == 0f);
    }

    [Fact]
    public void ToNormalized_does_not_mutate_the_source()
    {
        float[] v = [2f, 0f];

        float[] unit = VectorMath.ToNormalized(v);

        v.Should().Equal(2f, 0f);
        unit.Should().Equal(1f, 0f);
    }

    [Fact]
    public void For_unit_vectors_dot_product_equals_cosine_similarity()
    {
        float[] a = VectorMath.ToNormalized([0.3f, -1.2f, 4.4f, 0.01f, 9f]);
        float[] b = VectorMath.ToNormalized([1.3f, 2.2f, -0.4f, 3f, 1f]);

        VectorMath.Dot(a, b).Should().BeApproximately(VectorMath.CosineSimilarity(a, b), 1e-6f);
    }
}
