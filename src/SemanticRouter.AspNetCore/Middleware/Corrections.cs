using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Recognises a message that corrects the previous answer ("that's wrong", "it still doesn't compile", "no, I meant…"),
/// for <see cref="FeedbackKind.Corrected"/>. Phrases in English, Dutch, German, French and Spanish, anchored and short, so
/// new instructions that merely mention an error do not count. Other wording and other languages are left to
/// <see cref="CorrectionDetector"/>, which compares meaning.
/// </summary>
internal static partial class Corrections
{
    /// <summary>Whether <paramref name="prompt"/> reads as a correction of the previous answer.</summary>
    public static bool Matches(string prompt)
    {
        string text = prompt.Trim();
        if (text.Length == 0 || text.Length > 400)
        {
            return false;
        }

        return Leading().IsMatch(text) || (text.Length <= 200 && Anywhere().IsMatch(text));
    }

    // At the start of the message, per language: a plain "no,", "wrong", "that's not what I asked", "it (still) doesn't
    // work", "you forgot …", "try again".
    [GeneratedRegex(
        @"^(no\b[,.!]|nope\b|wrong\b|incorrect\b|that['’]?s (wrong|incorrect|not (right|it|what i))|this is (wrong|incorrect)|not what i (asked|meant|wanted)"
        + @"|(it|that|this) (still )?(doesn['’]?t|didn['’]?t|does not|did not|won['’]?t|will not) (work|compile|build|run|help|fix)"
        + @"|you (missed|forgot|ignored|broke)|try again\b"

        // Dutch
        + @"|nee\b[,.!]|(fout|verkeerd|onjuist)(\s*[,.!]|$)|dat (klopt|is) niet( goed| juist)?\b|(dit|dat) is (fout|verkeerd|onjuist)|niet wat ik (vroeg|bedoelde|wilde)"
        + @"|(het|dat|dit) werkt (nog steeds |nog altijd )?niet|(het|dat|dit) (compileert|bouwt) (nog steeds )?niet|je (bent|hebt) .{0,40}vergeten|je vergat|probeer (het )?opnieuw"

        // German
        + @"|nein\b[,.!]|falsch(\s*[,.!]|$)|(das|es) ist (falsch|nicht richtig)|das stimmt nicht|nicht (das|was) ich (wollte|meinte|gefragt habe)"
        + @"|(es|das) funktioniert (immer noch |noch immer )?nicht|(es|das) (kompiliert|baut) (immer noch |noch immer )?nicht|du hast .{0,40}vergessen|versuch(e|s)? es (noch ?mal|erneut)"

        // French
        + @"|non\b[,.!]|faux(\s*[,.!]|$)|c['’]est (faux|incorrect|pas (ça|correct|bon|juste|ce que je))|ce n['’]est pas (ça|correct|bon|juste|ce que je)"
        + @"|(ça|cela) ne (marche|fonctionne|compile) (toujours |plus )?pas|tu as (oublié|ignoré|cassé)|réessaie\b"

        // Spanish
        + @"|(eso|esto) (está|es) (mal|incorrecto)|está mal\b|incorrecto(\s*[,.!]|$)|no es (correcto|lo que (pedí|quería|quise decir))"
        + @"|(sigue|todavía) (sin funcionar|falla|fallando|no funciona)|no funciona\b|(te )?olvidaste|inténtalo (de nuevo|otra vez))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Leading();

    // Anywhere in a short message: "still failing", "same error", "doesn't work", per language.
    [GeneratedRegex(
        @"\b(still (fails|failing|broken|not working|wrong|crashes|crashing)|same error|(doesn['’]?t|didn['’]?t|does not|did not) (work|fix it|compile)"
        + @"|nog (steeds|altijd) (fout|kapot|niet)|(zelfde|dezelfde) fout(melding)?"
        + @"|(immer noch|noch immer) (falsch|kaputt|nicht)|(gleiche|derselbe|dieselbe|selbe|gleicher) Fehler"
        + @"|(toujours|encore) (faux|cassé|en échec|la même erreur)|même erreur|ne (marche|fonctionne) (toujours |plus )?pas"
        + @"|(sigue|todavía) (fallando|roto|sin funcionar|mal)|(el )?mismo error|no funciona)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Anywhere();
}
