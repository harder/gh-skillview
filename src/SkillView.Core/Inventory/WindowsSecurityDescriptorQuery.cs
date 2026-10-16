using SkillView.Maintenance;

namespace SkillView.Inventory;

/// Bounded transport for one complete native security descriptor. A changed
/// size or status refuses the observation rather than retrying a moving object.
internal static class WindowsSecurityDescriptorQuery
{
    internal static byte[] Read(Func<byte[]?, uint, (int Status, uint Needed)> query)
    {
        var sizing = query(null, 0);
        if (sizing.Status != unchecked((int)0xC0000023) || sizing.Needed is < 20 or > PayloadNativeMetadata.MaxEntryMetadataBytes)
            throw new IOException($"Complete Windows native security sizing refused: status=0x{sizing.Status:X8}, length={sizing.Needed}.");
        var bytes = new byte[sizing.Needed];
        var read = query(bytes, (uint)bytes.Length);
        if (read.Status != 0 || read.Needed != sizing.Needed)
            throw new IOException($"Complete Windows native security read changed/refused: status=0x{read.Status:X8}, expected={sizing.Needed}, actual={read.Needed}.");
        return bytes;
    }
}
