using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Checkpoint 13: TeX math markup — currency/code/streaming correctness (Android MathMarkup parity).
/// Typesetting acceptance lives in Checkpoint13WpfTests (actual WPF render).
/// </summary>
public class Checkpoint13Tests
{
    private static List<MathFormula> Formulas(string markdown) =>
        MathMarkup.ExtractFormulas(MathMarkup.Prepare(markdown)).ToList();

    [Fact]
    public void Prices_stay_as_they_are()
    {
        foreach (var text in new[]
                 {
                     "It costs $5 and $10 with tax.",
                     "Between $5-$10 a month",
                     "Pay $5 + $10 now",
                     "Prices: $1,000 to $2,500.",
                     "Only $3.50, or $4 after noon",
                     "A \\$5 bill and a \\$x\\$ sign",
                 })
        {
            Assert.Equal(text, MathMarkup.Prepare(text));
        }
    }

    [Fact]
    public void Inline_dollar_math_is_found()
    {
        Assert.Equal(
            new[] { new MathFormula("x^2 + y^2 = r^2", Display: false) },
            Formulas("A circle: $x^2 + y^2 = r^2$."));
        Assert.Equal(
            new[] { new MathFormula("x", false), new MathFormula("\\alpha", false) },
            Formulas("Let $x$ be $\\alpha$"));
        Assert.Equal(
            new[] { new MathFormula("a+b", false) },
            Formulas("Sum $a+b$ then"));
    }

    [Fact]
    public void Price_next_to_math_stays_a_price()
    {
        var prepared = MathMarkup.Prepare("It's $5, and $E = mc^2$ holds.");
        Assert.StartsWith("It's $5, and ", prepared);
        Assert.Equal(
            new[] { new MathFormula("E = mc^2", false) },
            Formulas("It's $5, and $E = mc^2$ holds."));
    }

    [Fact]
    public void Parenthesis_and_bracket_delimiters()
    {
        Assert.Equal(
            new[] { new MathFormula("\\frac{1}{2}", false) },
            Formulas("Half is \\(\\frac{1}{2}\\)."));
        Assert.Equal(
            new[] { new MathFormula("\\sum_{i=1}^n i", true) },
            Formulas("\\[ \\sum_{i=1}^n i \\]"));
    }

    [Fact]
    public void Display_math_on_own_lines_becomes_own_paragraph()
    {
        var prepared = MathMarkup.Prepare("The area:\n$$\n\\pi r^2\n$$\nwhere r is the radius.");
        Assert.Contains(MathMarkup.Scheme, prepared);
        Assert.EndsWith(")\n\nwhere r is the radius.", prepared);
        Assert.Equal(
            new[] { new MathFormula("\\pi r^2", true) },
            Formulas("The area:\n$$\n\\pi r^2\n$$\nwhere r is the radius."));
    }

    [Fact]
    public void Code_is_left_alone()
    {
        var text = "Use `$x^2$` in code, and:\n```bash\necho $HOME $PATH_x\n$$ not math $$\n```\nDone $y_1$";
        var prepared = MathMarkup.Prepare(text);
        Assert.Contains("```bash\necho $HOME $PATH_x\n$$ not math $$\n```", prepared);
        Assert.Equal(new[] { new MathFormula("y_1", false) }, Formulas(text));
    }

    [Fact]
    public void Unclosed_formula_stays_text_while_streaming()
    {
        Assert.Equal("The sum $$\\sum_i", MathMarkup.Prepare("The sum $$\\sum_i"));
        Assert.Equal("Let $x^", MathMarkup.Prepare("Let $x^"));
        Assert.Equal("```\n$x^2$", MathMarkup.Prepare("```\n$x^2$"));
    }

    [Fact]
    public void Spaces_inside_dollars_are_not_math()
    {
        Assert.Equal("a $ x^2 $ b", MathMarkup.Prepare("a $ x^2 $ b"));
    }

    [Fact]
    public void Escaped_brackets_stay_text()
    {
        foreach (var text in new[] { "See \\[1\\] and \\[2\\] for details", "Press \\[Enter\\]", "The regex \\(foo\\) matches" })
            Assert.Equal(text, MathMarkup.Prepare(text));
        Assert.Equal(new[] { new MathFormula("x", false) }, Formulas("Let \\(x\\) be"));
    }

    [Fact]
    public void LooksLikeMath_heuristics()
    {
        Assert.True(MathMarkup.LooksLikeMath("x"));
        Assert.True(MathMarkup.LooksLikeMath("x_1"));
        Assert.True(MathMarkup.LooksLikeMath("f = (a, b)"));
        Assert.True(MathMarkup.LooksLikeMath("(1, 2)"));
        Assert.True(MathMarkup.LooksLikeMath("a < b"));
        Assert.False(MathMarkup.LooksLikeMath("5 and "));
        Assert.False(MathMarkup.LooksLikeMath("1,000 to "));
        Assert.False(MathMarkup.LooksLikeMath("hello world"));
        Assert.False(MathMarkup.LooksLikeMath("HOME_DIR/"));
        Assert.Equal("Set $HOME_DIR/$USER/bin", MathMarkup.Prepare("Set $HOME_DIR/$USER/bin"));
    }

    [Fact]
    public void Plain_text_without_dollars_or_backslashes_is_unchanged_instance()
    {
        var text = "Plain *markdown* here";
        Assert.Same(text, MathMarkup.Prepare(text));
    }

    [Fact]
    public void Formula_links_round_trip_source()
    {
        var latex = "\\int_0^1 f(x)\\,dx = \\left[ F \\right]_0^1";
        Assert.Equal(new[] { new MathFormula(latex, true) }, Formulas("$$" + latex + "$$"));
        Assert.Null(MathMarkup.TryParseFormulaLink("https://example.com/x.png"));
        Assert.Null(MathMarkup.TryParseFormulaLink("herald-math:x:abc"));
    }

    [Fact]
    public void Oversize_latex_is_flagged_not_silently_dropped()
    {
        var huge = new string('x', MathMarkup.MaxLatexChars + 1);
        var prepared = MathMarkup.Prepare("$" + huge + "_1$");
        // Heuristic may not treat pure x-run as math; use tex-like content.
        prepared = MathMarkup.Prepare("$" + huge + "^2$");
        var formulas = Formulas("$" + huge + "^2$");
        Assert.Single(formulas);
        Assert.True(formulas[0].Latex.Length > MathMarkup.MaxLatexChars);
        Assert.True(MathMarkup.IsOversize(formulas[0].Latex));
    }
}
