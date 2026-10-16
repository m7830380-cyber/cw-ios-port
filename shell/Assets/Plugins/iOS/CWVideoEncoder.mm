// The game's video encoder library (PlayEveryWare "libve": VP8/WebM, Windows-only) re-implemented for iOS with
// AVFoundation. The patcher points NativeVeMethods at these CWVe_* functions and renames the game's clip files
// from .webm to .mp4, because the iOS video player only plays MP4/MOV (H.264 + AAC).
//
// How the game drives it (Zorro.Recorder.LibVEEncoder):
//   initialize_encoding(config)  once per clip (output file, 420x420, 24 fps, 24 kHz stereo)
//   encode_video_frame_from_memory  per frame: an array of 3 plane pointers (Y, U, V; I420), dim1 = Y bytes
//   encode_stereo_audio_sample   only when the clip ends: [L*, R*] planar float chunks of 1000 samples
//   finalize                     writes the file
// Video goes straight to H.264 while recording (a video-only temp file). Audio arrives after all the video, which
// AVAssetWriter cannot interleave, so finalize encodes it to a temp AAC file and muxes both (passthrough, fast).
// Stitching (LibVEStitcher): initialize_encoding + initialize_decoding + combine_webm_files(paths) + finalize
// concatenates the clips the same way.
#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#include <vector>
#include <stdint.h>
#include <unistd.h>

#if __has_feature(objc_arc)
#define CW_RELEASE(x) ((void)0)
#else
#define CW_RELEASE(x) [(x) release]
#endif

// Same layout as PlayEveryWare.VideoEncoding.ve_config as IL2CPP marshals it (LPWStr = UTF-16, bools = 1 byte).
struct CWVeConfig
{
    int32_t size_of;
    const uint16_t *output_file;
    int32_t video_codec;
    uint32_t width, height, fps;
    uint32_t video_buffer_prealloc_bytes, target_bitrate;
    int32_t min_quantizer, max_quantizer, lag_in_frames, kf_min_dist, kf_max_dist;
    int32_t buffer_format, frame_format, video_quality;
    uint32_t audio_sample_rate, audio_channels;
    float audio_quality;
    uint8_t audio_enable, overwrite_existing, num_threads;
    int8_t cpu_used;
};

static NSString *CWUtf16(const uint16_t *s)
{
    if (s == NULL) return nil;
    size_t n = 0;
    while (s[n] != 0) n++;
    return [NSString stringWithCharacters:(const unichar *)s length:n];
}

@interface CWVeEncoder : NSObject
{
@public
    std::vector<float> audio;   // interleaved stereo
}
@property (nonatomic, strong) NSString *outputPath;
@property (nonatomic, strong) NSString *videoTempPath;
@property (nonatomic, strong) NSString *audioTempPath;
@property (nonatomic, strong) AVAssetWriter *writer;
@property (nonatomic, strong) AVAssetWriterInput *videoInput;
@property (nonatomic, strong) AVAssetWriterInputPixelBufferAdaptor *adaptor;
@property (nonatomic, strong) NSMutableArray<NSString *> *stitchInputs;
@property (nonatomic) int width, height, fps, sampleRate, channels;
@property (nonatomic) int64_t frames;
@property (nonatomic) bool failed;
@end

@implementation CWVeEncoder
@end

static NSMutableDictionary<NSNumber *, CWVeEncoder *> *gEncoders;
static int64_t gNextHandle = 1;

static CWVeEncoder *CWEncoderFor(void *handle)
{
    @synchronized ([CWVeEncoder class])
    {
        return gEncoders[@((int64_t)(intptr_t)handle)];
    }
}

static void CWRemoveEncoder(void *handle)
{
    @synchronized ([CWVeEncoder class])
    {
        [gEncoders removeObjectForKey:@((int64_t)(intptr_t)handle)];
    }
}

static void CWRemoveFile(NSString *path)
{
    if (path != nil) [[NSFileManager defaultManager] removeItemAtPath:path error:nil];
}

// Waits (up to ~2 s) for an input to accept more data; recording must not silently drop frames.
static bool CWWaitReady(AVAssetWriterInput *input, AVAssetWriter *writer)
{
    for (int i = 0; i < 400; i++)
    {
        if (writer.status != AVAssetWriterStatusWriting) return false;
        if (input.readyForMoreMediaData) return true;
        usleep(5000);
    }
    return false;
}

static bool CWFinishWriter(AVAssetWriter *writer, NSString *what)
{
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    [writer finishWritingWithCompletionHandler:^{ dispatch_semaphore_signal(done); }];
    dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, 30 * NSEC_PER_SEC));
    if (writer.status != AVAssetWriterStatusCompleted)
    {
        NSLog(@"[CW-iOS] video encoder: finishing %@ failed (status %d): %@", what, (int)writer.status, writer.error);
        return false;
    }
    return true;
}

static bool CWStartVideo(CWVeEncoder *e)
{
    NSError *err = nil;
    CWRemoveFile(e.videoTempPath);
    AVAssetWriter *writer = [AVAssetWriter assetWriterWithURL:[NSURL fileURLWithPath:e.videoTempPath] fileType:AVFileTypeMPEG4 error:&err];
    if (writer == nil) { NSLog(@"[CW-iOS] video encoder: cannot create writer: %@", err); return false; }
    NSDictionary *settings = @{
        AVVideoCodecKey: AVVideoCodecTypeH264,
        AVVideoWidthKey: @(e.width),
        AVVideoHeightKey: @(e.height),
        AVVideoCompressionPropertiesKey: @{
            AVVideoAverageBitRateKey: @(2000000),
            AVVideoMaxKeyFrameIntervalKey: @(e.fps),
            AVVideoExpectedSourceFrameRateKey: @(e.fps),
            AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
        },
        // the game's capture shader writes BT.601 limited-range YUV (what libvpx expects)
        AVVideoColorPropertiesKey: @{
            AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
            AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
            AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_601_4,
        },
    };
    AVAssetWriterInput *input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo outputSettings:settings];
    input.expectsMediaDataInRealTime = YES;
    NSDictionary *pixelAttrs = @{
        (__bridge id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange),
        (__bridge id)kCVPixelBufferWidthKey: @(e.width),
        (__bridge id)kCVPixelBufferHeightKey: @(e.height),
        (__bridge id)kCVPixelBufferIOSurfacePropertiesKey: @{},
    };
    AVAssetWriterInputPixelBufferAdaptor *adaptor = [AVAssetWriterInputPixelBufferAdaptor assetWriterInputPixelBufferAdaptorWithAssetWriterInput:input sourcePixelBufferAttributes:pixelAttrs];
    if (![writer canAddInput:input]) { NSLog(@"[CW-iOS] video encoder: H.264 input rejected"); return false; }
    [writer addInput:input];
    if (![writer startWriting]) { NSLog(@"[CW-iOS] video encoder: startWriting failed: %@", writer.error); return false; }
    [writer startSessionAtSourceTime:kCMTimeZero];
    e.writer = writer;
    e.videoInput = input;
    e.adaptor = adaptor;
    return true;
}

// AAC-encodes the buffered interleaved float audio into e.audioTempPath.
static bool CWWriteAudio(CWVeEncoder *e)
{
    size_t totalFrames = e->audio.size() / 2;
    if (totalFrames == 0) return false;
    NSError *err = nil;
    CWRemoveFile(e.audioTempPath);
    AVAssetWriter *writer = [AVAssetWriter assetWriterWithURL:[NSURL fileURLWithPath:e.audioTempPath] fileType:AVFileTypeAppleM4A error:&err];
    if (writer == nil) { NSLog(@"[CW-iOS] video encoder: cannot create audio writer: %@", err); return false; }

    AudioStreamBasicDescription asbd = {0};
    asbd.mSampleRate = e.sampleRate;
    asbd.mFormatID = kAudioFormatLinearPCM;
    asbd.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
    asbd.mChannelsPerFrame = 2;
    asbd.mBitsPerChannel = 32;
    asbd.mBytesPerFrame = 8;
    asbd.mFramesPerPacket = 1;
    asbd.mBytesPerPacket = 8;
    AudioChannelLayout layout = {0};
    layout.mChannelLayoutTag = kAudioChannelLayoutTag_Stereo;
    CMAudioFormatDescriptionRef fmt = NULL;
    if (CMAudioFormatDescriptionCreate(kCFAllocatorDefault, &asbd, sizeof(layout), &layout, 0, NULL, NULL, &fmt) != noErr)
        return false;

    NSDictionary *settings = @{
        AVFormatIDKey: @(kAudioFormatMPEG4AAC),
        AVSampleRateKey: @(e.sampleRate),
        AVNumberOfChannelsKey: @2,
        AVChannelLayoutKey: [NSData dataWithBytes:&layout length:sizeof(layout)],
    };
    AVAssetWriterInput *input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeAudio outputSettings:settings sourceFormatHint:fmt];
    input.expectsMediaDataInRealTime = NO;
    if (![writer canAddInput:input]) { NSLog(@"[CW-iOS] video encoder: AAC input rejected"); CFRelease(fmt); return false; }
    [writer addInput:input];
    if (![writer startWriting]) { NSLog(@"[CW-iOS] video encoder: audio startWriting failed: %@", writer.error); CFRelease(fmt); return false; }
    [writer startSessionAtSourceTime:kCMTimeZero];

    bool ok = true;
    const size_t chunk = 4096;
    for (size_t at = 0; at < totalFrames && ok; at += chunk)
    {
        size_t n = MIN(chunk, totalFrames - at);
        size_t bytes = n * 8;
        CMBlockBufferRef block = NULL;
        CMSampleBufferRef sample = NULL;
        ok = CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault, NULL, bytes, kCFAllocatorDefault, NULL, 0, bytes, kCMBlockBufferAssureMemoryNowFlag, &block) == noErr
          && CMBlockBufferReplaceDataBytes(&e->audio[at * 2], block, 0, bytes) == noErr
          && CMAudioSampleBufferCreateReadyWithPacketDescriptions(kCFAllocatorDefault, block, fmt, (CMItemCount)n, CMTimeMake((int64_t)at, e.sampleRate), NULL, &sample) == noErr;
        if (ok)
        {
            for (int i = 0; i < 2000 && !input.readyForMoreMediaData && writer.status == AVAssetWriterStatusWriting; i++) usleep(2000);
            ok = input.readyForMoreMediaData && [input appendSampleBuffer:sample];
        }
        if (sample) CFRelease(sample);
        if (block) CFRelease(block);
    }
    CFRelease(fmt);
    [input markAsFinished];
    if (!ok) NSLog(@"[CW-iOS] video encoder: appending audio failed: %@", writer.error);
    return CWFinishWriter(writer, @"audio") && ok;
}

static bool CWExport(AVAsset *asset, NSString *outputPath)
{
    CWRemoveFile(outputPath);
    AVAssetExportSession *session = [AVAssetExportSession exportSessionWithAsset:asset presetName:AVAssetExportPresetPassthrough];
    if (session == nil) { NSLog(@"[CW-iOS] video encoder: no export session"); return false; }
    session.outputURL = [NSURL fileURLWithPath:outputPath];
    session.outputFileType = AVFileTypeMPEG4;
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    [session exportAsynchronouslyWithCompletionHandler:^{ dispatch_semaphore_signal(done); }];
    dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, 120 * NSEC_PER_SEC));
    if (session.status != AVAssetExportSessionStatusCompleted)
    {
        NSLog(@"[CW-iOS] video encoder: export to %@ failed (status %d): %@", outputPath.lastPathComponent, (int)session.status, session.error);
        [session cancelExport];
        return false;
    }
    return true;
}

static AVAssetTrack *CWFirstTrack(AVAsset *asset, AVMediaType type)
{
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    return [asset tracksWithMediaType:type].firstObject;
#pragma clang diagnostic pop
}

// Appends each file's video (and audio, when present) one after another.
static bool CWConcatenate(NSArray<NSString *> *inputs, NSString *outputPath)
{
    AVMutableComposition *comp = [AVMutableComposition composition];
    AVMutableCompositionTrack *v = [comp addMutableTrackWithMediaType:AVMediaTypeVideo preferredTrackID:kCMPersistentTrackID_Invalid];
    AVMutableCompositionTrack *a = nil;
    CMTime cursor = kCMTimeZero;
    for (NSString *path in inputs)
    {
        AVURLAsset *asset = [AVURLAsset URLAssetWithURL:[NSURL fileURLWithPath:path] options:@{ AVURLAssetPreferPreciseDurationAndTimingKey: @YES }];
        AVAssetTrack *vt = CWFirstTrack(asset, AVMediaTypeVideo);
        if (vt == nil) { NSLog(@"[CW-iOS] video encoder: no video in %@", path.lastPathComponent); continue; }
        CMTime dur = vt.timeRange.duration;
        NSError *err = nil;
        if (![v insertTimeRange:CMTimeRangeMake(kCMTimeZero, dur) ofTrack:vt atTime:cursor error:&err])
        {
            NSLog(@"[CW-iOS] video encoder: cannot add video of %@: %@", path.lastPathComponent, err);
            return false;
        }
        AVAssetTrack *at = CWFirstTrack(asset, AVMediaTypeAudio);
        if (at != nil)
        {
            if (a == nil) a = [comp addMutableTrackWithMediaType:AVMediaTypeAudio preferredTrackID:kCMPersistentTrackID_Invalid];
            CMTime adur = CMTimeMinimum(at.timeRange.duration, dur);
            if (![a insertTimeRange:CMTimeRangeMake(kCMTimeZero, adur) ofTrack:at atTime:cursor error:&err])
                NSLog(@"[CW-iOS] video encoder: cannot add audio of %@: %@", path.lastPathComponent, err);
        }
        cursor = CMTimeAdd(cursor, dur);
    }
    if (CMTimeCompare(cursor, kCMTimeZero) <= 0) return false;
    return CWExport(comp, outputPath);
}

static bool CWMux(CWVeEncoder *e, bool hasAudio)
{
    AVURLAsset *video = [AVURLAsset URLAssetWithURL:[NSURL fileURLWithPath:e.videoTempPath] options:nil];
    AVAssetTrack *vt = CWFirstTrack(video, AVMediaTypeVideo);
    if (vt == nil) { NSLog(@"[CW-iOS] video encoder: the recorded video has no track"); return false; }
    AVMutableComposition *comp = [AVMutableComposition composition];
    NSError *err = nil;
    CMTime dur = vt.timeRange.duration;
    AVMutableCompositionTrack *v = [comp addMutableTrackWithMediaType:AVMediaTypeVideo preferredTrackID:kCMPersistentTrackID_Invalid];
    if (![v insertTimeRange:CMTimeRangeMake(kCMTimeZero, dur) ofTrack:vt atTime:kCMTimeZero error:&err]) { NSLog(@"[CW-iOS] video encoder: mux video: %@", err); return false; }
    if (hasAudio)
    {
        AVURLAsset *audio = [AVURLAsset URLAssetWithURL:[NSURL fileURLWithPath:e.audioTempPath] options:nil];
        AVAssetTrack *at = CWFirstTrack(audio, AVMediaTypeAudio);
        if (at != nil)
        {
            AVMutableCompositionTrack *a = [comp addMutableTrackWithMediaType:AVMediaTypeAudio preferredTrackID:kCMPersistentTrackID_Invalid];
            if (![a insertTimeRange:CMTimeRangeMake(kCMTimeZero, CMTimeMinimum(at.timeRange.duration, dur)) ofTrack:at atTime:kCMTimeZero error:&err])
                NSLog(@"[CW-iOS] video encoder: mux audio: %@", err);
        }
    }
    return CWExport(comp, e.outputPath);
}

// ======================================================================================================
// libve API (C# bools are marshalled as one byte)
// ======================================================================================================
extern "C" bool CWVe_initialize(char *mountPointBuffer, uint32_t bufferSizeBytes)
{
    if (mountPointBuffer && bufferSizeBytes > 0) mountPointBuffer[0] = 0;
    return true;
}

extern "C" bool CWVe_cpu_has_required_simd_extensions() { return true; }

extern "C" bool CWVe_initialize_encoding(CWVeConfig *config, void **handle)
{
    @autoreleasepool
    {
        if (handle) *handle = NULL;
        if (config == NULL || handle == NULL) return false;
        NSString *path = CWUtf16(config->output_file);
        if (path.length == 0) { NSLog(@"[CW-iOS] video encoder: no output file"); return false; }
        if (config->size_of != (int32_t)sizeof(CWVeConfig))
            NSLog(@"[CW-iOS] video encoder: config size %d, expected %d", config->size_of, (int)sizeof(CWVeConfig));
        CWVeEncoder *e = [CWVeEncoder new];
        e.outputPath = path;
        e.videoTempPath = [path stringByAppendingString:@".video.mp4"];
        e.audioTempPath = [path stringByAppendingString:@".audio.m4a"];
        e.width = (int)(config->width ? config->width : 420);
        e.height = (int)(config->height ? config->height : 420);
        e.fps = (int)(config->fps ? config->fps : 24);
        e.sampleRate = (int)(config->audio_sample_rate ? config->audio_sample_rate : 24000);
        e.channels = (int)(config->audio_channels ? config->audio_channels : 2);
        [[NSFileManager defaultManager] createDirectoryAtPath:path.stringByDeletingLastPathComponent withIntermediateDirectories:YES attributes:nil error:nil];
        int64_t hid;
        @synchronized ([CWVeEncoder class])
        {
            if (gEncoders == nil) gEncoders = [NSMutableDictionary new];
            hid = gNextHandle++;
            gEncoders[@(hid)] = e;
        }
        CW_RELEASE(e);
        *handle = (void *)(intptr_t)hid;
        return true;
    }
}

extern "C" bool CWVe_encode_video_frame_from_memory(void *handle, uint8_t **planes, uint32_t planeCount, uint32_t yBytes, int32_t flags)
{
    @autoreleasepool
    {
        CWVeEncoder *e = CWEncoderFor(handle);
        if (e == nil || planes == NULL || planeCount < 3) return false;
        @synchronized (e)
        {
            if (e.failed) return false;
            if (e.writer == nil && !CWStartVideo(e)) { e.failed = true; return false; }
            int w = e.width, h = e.height, cw = (w + 1) / 2, ch = (h + 1) / 2;
            if (yBytes < (uint32_t)(w * h)) { NSLog(@"[CW-iOS] video encoder: frame is %u bytes, expected %d", yBytes, w * h); return false; }
            if (!CWWaitReady(e.videoInput, e.writer))
            {
                NSLog(@"[CW-iOS] video encoder: H.264 encoder not ready (status %d): %@", (int)e.writer.status, e.writer.error);
                if (e.writer.status == AVAssetWriterStatusFailed) e.failed = true;
                return false;
            }
            CVPixelBufferRef pb = NULL;
            if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, e.adaptor.pixelBufferPool, &pb) != kCVReturnSuccess || pb == NULL)
            {
                NSLog(@"[CW-iOS] video encoder: no pixel buffer");
                return false;
            }
            CVPixelBufferLockBaseAddress(pb, 0);
            uint8_t *dy = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pb, 0);
            size_t sy = CVPixelBufferGetBytesPerRowOfPlane(pb, 0);
            for (int r = 0; r < h; r++) memcpy(dy + r * sy, planes[0] + r * w, w);
            uint8_t *duv = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pb, 1);
            size_t suv = CVPixelBufferGetBytesPerRowOfPlane(pb, 1);
            const uint8_t *u = planes[1], *v = planes[2];
            for (int r = 0; r < ch; r++)
            {
                uint8_t *row = duv + r * suv;
                const uint8_t *ur = u + r * cw, *vr = v + r * cw;
                for (int c = 0; c < cw; c++) { row[2 * c] = ur[c]; row[2 * c + 1] = vr[c]; }
            }
            CVPixelBufferUnlockBaseAddress(pb, 0);
            CVBufferSetAttachment(pb, kCVImageBufferYCbCrMatrixKey, kCVImageBufferYCbCrMatrix_ITU_R_601_4, kCVAttachmentMode_ShouldPropagate);
            CVBufferSetAttachment(pb, kCVImageBufferColorPrimariesKey, kCVImageBufferColorPrimaries_ITU_R_709_2, kCVAttachmentMode_ShouldPropagate);
            CVBufferSetAttachment(pb, kCVImageBufferTransferFunctionKey, kCVImageBufferTransferFunction_ITU_R_709_2, kCVAttachmentMode_ShouldPropagate);
            bool ok = [e.adaptor appendPixelBuffer:pb withPresentationTime:CMTimeMake(e.frames, e.fps)];
            CVPixelBufferRelease(pb);
            if (!ok)
            {
                NSLog(@"[CW-iOS] video encoder: appending frame %lld failed: %@", e.frames, e.writer.error);
                if (e.writer.status == AVAssetWriterStatusFailed) e.failed = true;
                return false;
            }
            if (e.frames == 0) NSLog(@"[CW-iOS] video encoder: recording %dx%d @%d fps -> %@", w, h, e.fps, e.outputPath.lastPathComponent);
            e.frames++;
            return true;
        }
    }
}

extern "C" bool CWVe_encode_stereo_audio_sample(void *handle, float **channels, uint32_t count)
{
    CWVeEncoder *e = CWEncoderFor(handle);
    if (e == nil || channels == NULL) return false;
    @synchronized (e)
    {
        const float *l = channels[0], *r = channels[1];
        size_t at = e->audio.size();
        e->audio.resize(at + count * 2);
        for (uint32_t i = 0; i < count; i++) { e->audio[at + 2 * i] = l[i]; e->audio[at + 2 * i + 1] = r[i]; }
        return true;
    }
}

extern "C" bool CWVe_encode_mono_audio_sample(void *handle, float *samples, uint32_t count)
{
    CWVeEncoder *e = CWEncoderFor(handle);
    if (e == nil || samples == NULL) return false;
    @synchronized (e)
    {
        size_t at = e->audio.size();
        e->audio.resize(at + count * 2);
        for (uint32_t i = 0; i < count; i++) e->audio[at + 2 * i] = e->audio[at + 2 * i + 1] = samples[i];
        return true;
    }
}

extern "C" bool CWVe_finalize(void *handle)
{
    @autoreleasepool
    {
        CWVeEncoder *e = CWEncoderFor(handle);
        if (e == nil) return false;
        bool ok = false;
        @synchronized (e)
        {
            if (e.stitchInputs != nil)
            {
                ok = CWConcatenate(e.stitchInputs, e.outputPath);
                NSLog(@"[CW-iOS] video encoder: joined %d clips -> %@: %s", (int)e.stitchInputs.count, e.outputPath.lastPathComponent, ok ? "ok" : "FAILED");
            }
            else if (e.writer != nil && e.frames > 0 && !e.failed)
            {
                [e.videoInput markAsFinished];
                ok = CWFinishWriter(e.writer, @"video");
                if (ok)
                {
                    bool hasAudio = CWWriteAudio(e);
                    ok = CWMux(e, hasAudio);
                    if (!ok && !hasAudio)
                    {
                        // keep at least the picture
                        CWRemoveFile(e.outputPath);
                        ok = [[NSFileManager defaultManager] moveItemAtPath:e.videoTempPath toPath:e.outputPath error:nil];
                    }
                    NSLog(@"[CW-iOS] video encoder: %lld frames, %.1f s audio -> %@: %s", e.frames,
                          e->audio.size() / 2.0 / e.sampleRate, e.outputPath.lastPathComponent, ok ? "ok" : "FAILED");
                }
            }
            else
            {
                NSLog(@"[CW-iOS] video encoder: nothing to finalize (%lld frames%s)", e.frames, e.failed ? ", encoder failed" : "");
                if (e.writer != nil) [e.writer cancelWriting];
            }
            CWRemoveFile(e.videoTempPath);
            CWRemoveFile(e.audioTempPath);
            e.writer = nil;
            e.videoInput = nil;
            e.adaptor = nil;
            std::vector<float>().swap(e->audio);
        }
        CWRemoveEncoder(handle);
        return ok;
    }
}

extern "C" bool CWVe_try_force_release(void *handle)
{
    @autoreleasepool
    {
        CWVeEncoder *e = CWEncoderFor(handle);
        if (e == nil) return true;
        @synchronized (e)
        {
            if (e.writer != nil && e.writer.status == AVAssetWriterStatusWriting) [e.writer cancelWriting];
            CWRemoveFile(e.videoTempPath);
            CWRemoveFile(e.audioTempPath);
            e.writer = nil;
        }
        CWRemoveEncoder(handle);
        return true;
    }
}

extern "C" bool CWVe_initialize_decoding(CWVeConfig *config, void *handle) { return CWEncoderFor(handle) != nil; }

extern "C" bool CWVe_combine_webm_files(void *handle, const uint16_t **paths, int32_t length)
{
    @autoreleasepool
    {
        CWVeEncoder *e = CWEncoderFor(handle);
        if (e == nil || paths == NULL) return false;
        @synchronized (e)
        {
            NSMutableArray<NSString *> *list = [NSMutableArray array];
            for (int i = 0; i < length; i++)
            {
                NSString *p = CWUtf16(paths[i]);
                if (p.length > 0) [list addObject:p];
            }
            e.stitchInputs = list;
            return list.count > 0;
        }
    }
}
