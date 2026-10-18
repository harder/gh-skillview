using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;

namespace SkillView.PackageChecking;

internal sealed class StaticPackageChecker
{
    internal sealed record Limits(int MarkdownFiles = 128, int DocumentBytes = 1024 * 1024,
        int SkillBytes = PayloadSnapshot.MaxSkillBytes, int TextBytes = 8 * 1024 * 1024,
        int References = 4096, int Findings = 256);

    private readonly Limits limits;
    private readonly Func<string, Action<NativePayloadItem, PayloadEntry>, CancellationToken, PayloadSnapshot> capture;
    private readonly PackageAgentTarget? agent;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal StaticPackageChecker(Limits? limits = null,
        Func<string, Action<NativePayloadItem, PayloadEntry>, CancellationToken, PayloadSnapshot>? captureForTests = null,
        PackageAgentTarget? agent = null)
    {
        this.limits = limits ?? new();
        if (this.limits.MarkdownFiles is < 1 or > 128 || this.limits.DocumentBytes is < 1 or > 1024 * 1024
            || this.limits.SkillBytes is < 1 or > PayloadSnapshot.MaxSkillBytes || this.limits.TextBytes < this.limits.SkillBytes
            || this.limits.TextBytes > 8 * 1024 * 1024 || this.limits.References is < 1 or > 4096 || this.limits.Findings is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limits));
        capture = captureForTests ?? PayloadSnapshot.CaptureWithEntrySink;
        this.agent = agent;
    }

    internal Task<PackageCheckReport> CheckAsync(InstalledSkill target, CancellationToken token) =>
        Task.Run(() => Check(target, token), token);

    private PackageCheckReport Check(InstalledSkill target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var texts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var findings = ImmutableArray.CreateBuilder<PackageCheckFinding>();
        var observations = ImmutableArray.CreateBuilder<PackageObservation>();
        var declarationReasons = new HashSet<string>(StringComparer.Ordinal);
        var observationCount = 0;
        string? rootText = null, rootMetadataSha256 = null;
        var findingCount = 0; var hasErrors = false; var referenceErrors = false; var format = "unknown";
        var textBytes = 0; var documents = 0; var references = 0; var analyzedMarkdown = 0;
        PayloadSnapshot snapshot;
        try
        {
            snapshot = capture(target.ResolvedPath, (item, entry) =>
            {
                token.ThrowIfCancellationRequested();
                var markdown = entry.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
                if (entry.Kind != "file")
                {
                    if (markdown || PackageDeclarationScanner.Select(entry.Path))
                        declarationReasons.Add(entry.Path + ": selected declaration/script is not a regular file; links are not followed.");
                    if (markdown) reasons.Add(entry.Path + ": selected Markdown is not a regular file; links are not followed.");
                    return;
                }
                if (!markdown && !PackageDeclarationScanner.Select(entry.Path)) return;
                var skill = entry.Path == "SKILL.md";
                var cap = skill ? limits.SkillBytes : limits.DocumentBytes;
                // Reserve a root SKILL.md slot and byte budget regardless of native enumeration order.
                var used = texts.Where(pair => pair.Key != "SKILL.md").Sum(pair => pair.Value.Length);
                if (entry.Bytes > cap || entry.Bytes > limits.TextBytes - textBytes
                    || !skill && (documents >= limits.MarkdownFiles - 1 || entry.Bytes > limits.TextBytes - limits.SkillBytes - used))
                {
                    declarationReasons.Add("Text file/count/byte limit omitted one or more documents.");
                    if (markdown) reasons.Add("Text file/count/byte limit omitted one or more documents.");
                    return;
                }
                var input = item.Content ?? throw new IOException("Held selected-text stream is unavailable.");
                var bytes = Read(input, checked((int)entry.Bytes), cap, token);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != entry.ContentHash)
                    throw new IOException("Held selected-text bytes differ from the captured content fingerprint.");
                texts.Add(entry.Path, bytes);
                textBytes += bytes.Length;
                if (!skill) documents++;
            }, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A failed final native verification invalidates earlier traversal callbacks.
            // Independently capture regular root metadata; never reuse a partial path index.
            texts.Clear(); reasons.Clear(); observations.Clear(); declarationReasons.Clear(); observationCount = 0;
            reasons.Add("Whole-payload observation unavailable: " + exception.Message);
            try
            {
                RegularFileInspection.Capture(Path.Combine(target.ResolvedPath, "SKILL.md"), item =>
                {
                    var stream = item.Content ?? throw new IOException("Regular root metadata stream unavailable.");
                    var bytes = Read(stream, checked((int)Math.Min(stream.Length, int.MaxValue)), limits.SkillBytes, token);
                    CheckMetadata(bytes);
                    return new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
                }, token);
            }
            catch (Exception metadataException) when (metadataException is IOException or UnauthorizedAccessException or NotSupportedException or OverflowException)
            { format = "unknown"; rootText = null; rootMetadataSha256 = null; findings.Clear(); observations.Clear(); observationCount = 0; declarationReasons.Clear(); findingCount = 0; hasErrors = false; reasons.Add("Root metadata observation unavailable: " + metadataException.Message); }
            return Result("unavailable", "unavailable", "unknown", null, 0, 0, 0);
        }

        var index = new Dictionary<string, PayloadEntry>(StringComparer.Ordinal);
        var caseIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Entries)
        {
            token.ThrowIfCancellationRequested();
            index.Add(entry.Path, entry);
            if (!caseIndex.TryGetValue(entry.Path, out var paths)) caseIndex.Add(entry.Path, paths = []);
            paths.Add(entry.Path);
            PackageDeclarationScanner.PathPortability(entry.Path, Observe);
        }
        foreach (var collision in caseIndex.Values.Where(paths => paths.Count > 1))
            Observe(new("portability", "case-collision", collision[0], null, string.Join(", ", collision.Take(4)), "review",
                "These captured paths differ only by case. Review distinct portable names and authored references before sharing on case-insensitive filesystems."));
        if (!index.TryGetValue("SKILL.md", out var skillEntry))
        {
            format = "missing";
            Add("metadata", "skill-file-missing", "SKILL.md", null, null,
                "The package has no exact root SKILL.md entry.", "Add the required root SKILL.md with name and description metadata.");
        }
        else if (skillEntry.Kind != "file")
        { reasons.Add("Root SKILL.md is not a regular file; links are not followed."); }
        else if (texts.TryGetValue("SKILL.md", out var skillBytes)) CheckMetadata(skillBytes);
        else reasons.Add("Root SKILL.md exceeded its metadata byte budget.");

        var anchors = new Dictionary<string, PackageAnchorScan>(StringComparer.Ordinal);
        foreach (var (path, bytes) in texts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                declarationReasons.Add("One or more selected text documents are not valid UTF-8.");
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) reasons.Add("One or more selected text documents are not valid UTF-8.");
                continue;
            }
            PackageDeclarationScanner.Scan(path, text, Observe, reason => declarationReasons.Add(reason),
                reference =>
                {
                    if (references < limits.References) { references++; InspectReference(path, reference); }
                    else reasons.Add("Reference count limit reached; later local dependencies were not inspected.");
                }, token);
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            if (references >= limits.References) { reasons.Add("Reference count limit reached; later documents were not inspected."); continue; }
            var offset = 0;
            if (path == "SKILL.md")
            {
                var body = MarkdownBody(text);
                if (!body.Known)
                { reasons.Add("Root frontmatter has no bounded closing delimiter; its Markdown body was not inspected."); continue; }
                (text, offset) = (body.Text, body.Offset);
            }
            var parsed = MarkdownPackageReferences.Scan(text, limits.References - references, token, offset);
            analyzedMarkdown++;
            foreach (var reason in parsed.Reasons) reasons.Add(reason);
            foreach (var reference in parsed.References)
            {
                token.ThrowIfCancellationRequested(); references++;
                if (reference.UndefinedLabel)
                { reasons.Add("Undefined explicit reference labels were not resolved."); continue; }
                InspectReference(path, reference);
            }
        }
        var unknownReferences = reasons.Count != 0;
        return Result("complete", unknownReferences ? "partial" : "limited",
            referenceErrors ? "findings" : unknownReferences ? "unknown" : "none-observed",
            snapshot.Fingerprint, snapshot.Entries.Length, snapshot.Entries.Sum(entry => entry.Bytes), analyzedMarkdown);

        void CheckMetadata(byte[] bytes)
        {
            rootMetadataSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                format = "invalid";
                Add("metadata", "metadata-encoding", "SKILL.md", null, null, "Root SKILL.md is not valid UTF-8.", "Review and save the metadata as valid UTF-8.");
                return;
            }
            rootText = text;
            var (_, metadata, parsed) = FrontMatterParser.Parse(text);
            if (!parsed)
            {
                format = "invalid";
                Add("metadata", "metadata-framing-or-yaml", "SKILL.md", null, null,
                    "Required bounded frontmatter could not be parsed: " + string.Join("; ", metadata.Diagnostics),
                    "Review the opening/closing YAML delimiters and metadata syntax; no bytes were edited.");
                return;
            }
            format = metadata.Diagnostics.IsEmpty ? "valid" : "invalid";
            if (metadata.Compatibility is { } compatibility)
                Observe(new("dependency", "authored-compatibility", "SKILL.md", null, compatibility, "availability-unknown",
                    "Review these authored prerequisites and provide them in your explicitly chosen environment; no tools, credentials or dependencies were probed."));
            foreach (var tool in metadata.AllowedTools)
                Observe(new("declaration", "allowed-tools", "SKILL.md", null, tool, "text-only",
                    "Review this authored tool declaration against the selected agent's documented syntax. It is neither an effective permission nor evidence that the tool is available."));
            PackageDeclarationScanner.HeaderShapes(text, reason => declarationReasons.Add(reason), token);
            foreach (var diagnostic in metadata.Diagnostics)
            {
                token.ThrowIfCancellationRequested();
                var required = diagnostic.StartsWith("Required name", StringComparison.Ordinal) ? "name"
                    : diagnostic.StartsWith("Required description", StringComparison.Ordinal) ? "description" : null;
                Add("metadata", required is null ? "metadata-invalid" : "metadata-required-" + required, "SKILL.md", null, null,
                    diagnostic, required is null ? "Review the reported metadata format; no payload repair was attempted." : "Add a nonempty " + required + " field in root SKILL.md frontmatter.");
            }
        }

        void InspectReference(string document, PackageMarkdownReference reference)
        {
            var targetText = reference.Target;
            int? sourceLine = reference.Line > 0 ? reference.Line : null;
            string? anchor = null;
            if (reference.DecodeUrl)
            {
                var fragment = targetText.IndexOf('#');
                if (fragment >= 0) { anchor = targetText[(fragment + 1)..]; targetText = targetText[..fragment]; }
                var query = targetText.IndexOf('?');
                if (query >= 0) targetText = targetText[..query];
                for (var index = 0; index < targetText.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (targetText[index] != '%') continue;
                    if (index + 2 >= targetText.Length || !char.IsAsciiHexDigit(targetText[index + 1]) || !char.IsAsciiHexDigit(targetText[index + 2]))
                    { reasons.Add("Malformed URL escape was not inspected."); return; }
                    index += 2;
                }
                try { targetText = Uri.UnescapeDataString(targetText); }
                catch (UriFormatException) { reasons.Add("Malformed URL escape was not inspected."); return; }
            }
            if (targetText.Length == 0) { if (anchor is { Length: > 0 }) InspectAnchor(document, document, anchor, reference); return; }
            if (targetText.StartsWith("//", StringComparison.Ordinal)) return;
            if (targetText.Contains(':'))
            {
                if (targetText.Length > 2 && char.IsAsciiLetter(targetText[0]) && targetText[1] == ':' && targetText[2] is '/' or '\\')
                {
                    reasons.Add("An environment-specific Windows drive reference was not inspected.");
                    Observe(new("portability", "nonportable-reference", document, sourceLine, reference.Target, "review",
                        "Review a package-relative reference or explicitly document its required Windows drive layout in the authored source."));
                }
                return;
            }
            if (targetText[0] == '/' || targetText.Contains('\\') || targetText.Any(char.IsControl)
                || targetText.IndexOfAny(['$', '*', '{', '}']) >= 0)
            {
                reasons.Add("Absolute, dynamic or unsupported reference paths were not inspected.");
                Observe(new("portability", "nonportable-reference", document, sourceLine, reference.Target, "review",
                    "Replace environment-specific path syntax with a reviewed package-relative path or document its required platform/environment in the authored source."));
                return;
            }
            var segments = document.Split('/').SkipLast(1).ToList();
            foreach (var segment in targetText.Split('/'))
            {
                token.ThrowIfCancellationRequested();
                if (segment is "" or ".") continue;
                if (segment == "..")
                {
                    if (segments.Count == 0) { reasons.Add("A reference escapes the captured package and was not inspected."); return; }
                    segments.RemoveAt(segments.Count - 1);
                }
                else
                {
                    segments.Add(segment);
                    // A lexical '..' later in the reference cannot erase the fact
                    // that reaching it first traverses an uninspected native link.
                    if (CapturedLink(string.Join('/', segments)))
                    { reasons.Add("References through captured links were not followed."); return; }
                }
            }
            var relative = string.Join('/', segments);
            for (var count = 1; count <= segments.Count; count++)
            {
                token.ThrowIfCancellationRequested();
                var prefix = string.Join('/', segments.Take(count));
                if (CapturedLink(prefix))
                { reasons.Add("References through captured links were not followed."); return; }
            }
            if (relative.Length == 0) return;
            if (index.ContainsKey(relative))
            { if (anchor is { Length: > 0 }) InspectAnchor(document, relative, anchor, reference); return; }
            if (caseIndex.TryGetValue(relative, out var alternatives))
                Add("reference", "reference-case-mismatch", document, sourceLine, reference.Target,
                    "The target differs in case from captured path(s): " + string.Join(", ", alternatives.Take(4)),
                    "Use the exact captured path spelling; review case portability before sharing the package.");
            else
                Add("reference", reference.Dependency ? "dependency-file-missing" : "reference-missing", document, sourceLine, reference.Target,
                    "The internal target is absent from the complete captured package: " + relative,
                    reference.Dependency ? $"Restore the declared local dependency {relative} or correct its declaration in {document}; provision external dependencies only in an explicitly chosen environment."
                        : "Restore the referenced file/asset/script or review and correct this explicit reference.");
        }

        void InspectAnchor(string document, string relative, string fragment, PackageMarkdownReference reference)
        {
            if (!texts.TryGetValue(relative, out var bytes) || !relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            { reasons.Add("Fragment target is not a completely retained Markdown document."); return; }
            if (!anchors.TryGetValue(relative, out var parsed))
            {
                string text;
                try { text = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { reasons.Add("Fragment target encoding is unavailable."); return; }
                if (relative == "SKILL.md")
                {
                    var body = MarkdownBody(text);
                    if (!body.Known) { reasons.Add("Fragment target frontmatter closing delimiter was not established."); return; }
                    text = body.Text;
                }
                parsed = MarkdownPackageReferences.Anchors(text, token); anchors.Add(relative, parsed);
            }
            // This grammar deliberately supports only literal ASCII IDs. No
            // guessed renderer/HTML/Unicode slug may prove a missing anchor.
            if (fragment.Length > 4096 || fragment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            { reasons.Add("Unsupported fragment ID syntax was not resolved."); return; }
            if (parsed.Anchors.Contains(fragment)) return;
            if (!parsed.Reasons.IsEmpty)
            { foreach (var reason in parsed.Reasons) reasons.Add(reason); return; }
            if (parsed.Anchors.Any(id => id.Equals(fragment, StringComparison.OrdinalIgnoreCase)))
                Add("reference", "reference-anchor-case-mismatch", document, reference.Line, reference.Target,
                    "The fragment differs in case from a supported captured heading ID.", "Use the exact lower-case captured heading ID, or review the intended renderer's anchor syntax.");
            else Add("reference", "reference-anchor-missing", document, reference.Line, reference.Target,
                "The fragment is absent from the supported captured plain ASCII ATX heading IDs in " + relative + ".",
                "Correct the fragment to a captured heading ID or restore the intended authored heading; unsupported renderer-specific IDs require separate review.");
        }

        bool CapturedLink(string prefix)
        {
            token.ThrowIfCancellationRequested();
            if (index.TryGetValue(prefix, out var exact) && exact.Kind == "link") return true;
            if (caseIndex.TryGetValue(prefix, out var candidates))
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (index[candidate].Kind == "link") return true;
                }
            return false;
        }

        void Add(string category, string code, string path, int? line, string? targetPath, string message, string repair)
        {
            findingCount++; hasErrors = true;
            referenceErrors |= category == "reference";
            if (findings.Count < limits.Findings) findings.Add(new(category, code, path, line, targetPath, message, repair));
        }
        void Observe(PackageObservation observation)
        {
            observationCount++;
            if (observation.Value.Length > 4096)
            {
                declarationReasons.Add("An observation exceeded its 4096-character value limit; retained text is partial.");
                observation = observation with { Value = observation.Value[..4096], State = "unknown" };
            }
            if (observations.Count < limits.Findings) observations.Add(observation);
        }
        PackageCheckReport Result(string payload, string referenceCoverage, string referenceStatus, string? fingerprint, int entries, long bytes, int analyzed)
        {
            if (payload != "complete") declarationReasons.Add("Whole-payload observation was unavailable; selected declaration text is incomplete.");
            if (observationCount > observations.Count) declarationReasons.Add("Observation retention limit omitted evidence; see exact omission counts.");
            return new(target.InstallationId, target.ResolvedPath, format, referenceStatus,
                new(payload, referenceCoverage, entries, bytes, analyzed, references, reasons.Order(StringComparer.Ordinal).ToImmutableArray()),
                fingerprint, findings.ToImmutable(), findingCount, findingCount - findings.Count, hasErrors)
            {
                RootMetadataSha256 = rootMetadataSha256,
                AgentAssessment = PackageCompatibilityChecker.Check(agent, rootText, rootMetadataSha256),
                Observations = observations.ToImmutable(),
                ObservationCount = observationCount,
                DeclarationCoverage = payload == "complete" && declarationReasons.Count == 0 && observationCount <= limits.Findings ? "limited" : "partial",
                DeclarationReasons = declarationReasons.Order(StringComparer.Ordinal).ToImmutableArray()
            };
        }
    }

    private static byte[] Read(Stream input, int expected, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expected < 0 || expected > limit || input.Length != expected) throw new IOException("Selected metadata/text byte budget exceeded.");
        var bytes = new byte[expected]; var read = 0;
        while (read < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = input.Read(bytes, read, Math.Min(64 * 1024, bytes.Length - read));
            if (count == 0) throw new IOException("Captured text stream ended before its observed length.");
            read += count;
        }
        if (input.Length != expected) throw new IOException("Captured text length changed.");
        return bytes;
    }

    private static (string Text, int Offset, bool Known) MarkdownBody(string text)
    {
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        if (reader.ReadLine() != "---") return (text, 0, true);
        var lines = 1; var read = 4;
        while (reader.ReadLine() is { } line && read <= FrontMatterParser.MaxYamlChars)
        {
            lines++; read += line.Length + 1;
            if (line is "---" or "...") return (reader.ReadToEnd(), lines, true);
        }
        return ("", 0, false);
    }
}
