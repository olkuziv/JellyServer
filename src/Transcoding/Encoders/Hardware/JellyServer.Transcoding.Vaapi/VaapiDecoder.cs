namespace JellyServer.Transcoding.Vaapi;

/// <summary>
/// VAAPI decoding for software encoders: the GPU decodes, deinterlaces, scales and tone-maps, then the
/// frames are downloaded as nv12 for the encoder.
/// </summary>
internal sealed class VaapiDecoder(VaapiProbe probe) : IVideoDecoder
{
    public string Id => "vaapi";

    public async Task<bool> ProbeAsync(IFfmpegCapabilities ffmpeg, CancellationToken ct) => (await probe.ProbeAsync(ffmpeg, ct)).Decodes.Count > 0;

    public bool CanDecode(VideoJob job) => VaapiChain.Build(probe.Current, job, toMemory: true) is not null;

    public DecodedVideo Build(VideoJob job)
    {
        var (input, filters) = VaapiChain.Build(probe.Current, job, toMemory: true)
            ?? throw new InvalidOperationException("VAAPI cannot decode this source with the job's settings");
        return new DecodedVideo(Id, input, filters);
    }
}
