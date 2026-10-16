namespace SkillView.Maintenance;

// Byte evidence has no implicit directory/root authority. In particular, a
// captured resolution file must not fabricate a whole native payload snapshot.
internal interface IPayloadEvidenceReader
{
    PayloadEntry Entry(string path);
    PayloadNativeMetadata? Metadata(string path);
    void CopyEntry(string path, Stream destination, CancellationToken token);
}
