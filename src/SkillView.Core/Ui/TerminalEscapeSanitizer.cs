using System.Text;

namespace SkillView.Ui;

/// <summary>
/// Strips terminal escape sequences from untrusted content before it reaches
/// Terminal.Gui views or terminal-rendered output.
/// </summary>
internal static class TerminalEscapeSanitizer
{
    public static string? Sanitize(string? input)
    {
        if (input is null)
        {
            return null;
        }

        if (input.Length == 0)
        {
            return string.Empty;
        }

        if (!ContainsDangerousBytes(input))
        {
            return input;
        }

        var sb = new StringBuilder(input.Length);

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            switch (c)
            {
                case '\x1b':
                    if (i + 1 >= input.Length)
                    {
                        break;
                    }

                    var next = input[i + 1];
                    if (next == '[')
                    {
                        i += 2;
                        while (i < input.Length)
                        {
                            var seqChar = input[i];
                            if (seqChar >= '@' && seqChar <= '~')
                            {
                                break;
                            }

                            i++;
                        }
                    }
                    else if (next == ']')
                    {
                        i += 2;
                        while (i < input.Length)
                        {
                            var osc = input[i];
                            if (osc == '\x07')
                            {
                                break;
                            }

                            if (osc == '\x1b' && i + 1 < input.Length && input[i + 1] == '\\')
                            {
                                i++;
                                break;
                            }

                            i++;
                        }
                    }
                    else if (next >= '@' && next <= '_')
                    {
                        i++;
                    }
                    break;

                case '\x07':
                case '\x9b':
                case '\x9d':
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    public static string SanitizeRenderedOutput(string renderedAnsi)
    {
        if (string.IsNullOrEmpty(renderedAnsi))
        {
            return renderedAnsi ?? string.Empty;
        }

        if (!ContainsDangerousOutputBytes(renderedAnsi))
        {
            return renderedAnsi;
        }

        var sb = new StringBuilder(renderedAnsi.Length);

        for (var i = 0; i < renderedAnsi.Length; i++)
        {
            var c = renderedAnsi[i];

            switch (c)
            {
                case '\x1b':
                    if (i + 1 < renderedAnsi.Length)
                    {
                        var next = renderedAnsi[i + 1];

                        if (next == '[')
                        {
                            sb.Append('\x1b');
                            sb.Append('[');
                            i += 2;

                            while (i < renderedAnsi.Length)
                            {
                                var seqChar = renderedAnsi[i];
                                sb.Append(seqChar);

                                if (seqChar >= '@' && seqChar <= '~')
                                {
                                    break;
                                }

                                i++;
                            }
                        }
                        else if (next == ']')
                        {
                            if (i + 2 < renderedAnsi.Length && renderedAnsi[i + 2] == '8')
                            {
                                sb.Append('\x1b');
                                sb.Append(']');
                                i += 2;

                                while (i < renderedAnsi.Length)
                                {
                                    var osc = renderedAnsi[i];

                                    if (osc == '\x07')
                                    {
                                        sb.Append(osc);
                                        break;
                                    }

                                    if (osc == '\x1b' && i + 1 < renderedAnsi.Length && renderedAnsi[i + 1] == '\\')
                                    {
                                        sb.Append('\x1b');
                                        sb.Append('\\');
                                        i++;
                                        break;
                                    }

                                    sb.Append(osc);
                                    i++;
                                }
                            }
                            else
                            {
                                i += 2;

                                while (i < renderedAnsi.Length)
                                {
                                    var osc = renderedAnsi[i];

                                    if (osc == '\x07')
                                    {
                                        break;
                                    }

                                    if (osc == '\x1b' && i + 1 < renderedAnsi.Length && renderedAnsi[i + 1] == '\\')
                                    {
                                        i++;
                                        break;
                                    }

                                    i++;
                                }
                            }
                        }
                        else if (next >= '@' && next <= '_')
                        {
                            i++;
                        }
                    }
                    break;

                case '\x07':
                case '\x9b':
                case '\x9d':
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool ContainsDangerousBytes(string input)
    {
        foreach (var c in input)
        {
            if (c is '\x1b' or '\x07' or '\x9b' or '\x9d')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDangerousOutputBytes(string input)
    {
        foreach (var c in input)
        {
            if (c is '\x07' or '\x9b' or '\x9d')
            {
                return true;
            }
        }

        for (var i = 0; i < input.Length - 1; i++)
        {
            if (input[i] != '\x1b')
            {
                continue;
            }

            var next = input[i + 1];
            if (next >= '@' && next <= '_' && next != '[')
            {
                return true;
            }
        }

        return false;
    }
}
