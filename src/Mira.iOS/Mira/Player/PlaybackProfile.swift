import AVFoundation
import VideoToolbox

/// What this iPhone plays, told to Jellyfin as a device profile (profile.js of Mira web, for Apple's player): the file
/// as it is when AVPlayer reads it (MP4, MOV), otherwise a conversion into HLS fMP4, H.264 first.
enum PlaybackProfile {
    /// What the iPhone decodes, read once.
    struct Device: Sendable, Hashable {
        var hevc = true
        var av1 = false
        /// HDR10, HLG and Dolby Vision (profiles 5 and 8) on a screen that shows them.
        var hdr = true
        /// The highest HEVC level sent as it is, as Jellyfin writes it (level × 30).
        var hevcLevel = 156

        @MainActor static func read() -> Device {
            let av1 = VTIsHardwareDecodeSupported(kCMVideoCodecType_AV1)
            return Device(hevc: VTIsHardwareDecodeSupported(kCMVideoCodecType_HEVC), av1: av1, hdr: AVPlayer.eligibleForHDRPlayback, hevcLevel: av1 ? 186 : 156)
        }
    }

    private static func condition(_ condition: String, _ property: String, _ value: String, required: Bool = false) -> [String: Any] {
        ["Condition": condition, "Property": property, "Value": value, "IsRequired": required]
    }

    private static func h264(maxLevel: Int, maxWidth: Int? = nil) -> [String: Any] {
        var conditions = [
            condition("LessThanEqual", "VideoBitDepth", "8"),
            condition("EqualsAny", "VideoProfile", "high|main|baseline|constrained baseline"),
            condition("LessThanEqual", "VideoLevel", String(maxLevel)),
            condition("EqualsAny", "VideoRangeType", "SDR"),
            condition("NotEquals", "IsInterlaced", "true"),
        ]
        if let maxWidth { conditions.insert(condition("LessThanEqual", "Width", String(maxWidth)), at: 2) }
        return ["Type": "Video", "Codec": "h264", "Conditions": conditions]
    }

    /// Subtitles: text ones in the HLS stream of a conversion, where Apple's menu lists them and AirPlay carries them.
    /// None beside the video: AVPlayer reads no subtitle file of its own, so text subtitles chosen for a file sent as it
    /// is make Jellyfin copy it into HLS with them (the video copied, not converted). Pictures (PGS, DVD) are drawn
    /// into the video.
    private static let subtitles: [[String: Any]] = [
        ["Format": "vtt", "Method": "Hls"],
        ["Format": "pgssub", "Method": "Encode"],
        ["Format": "dvdsub", "Method": "Encode"],
        ["Format": "dvbsub", "Method": "Encode"],
    ]

    /// The profile Jellyfin's PlaybackInfo decides with; `bitrate` caps both the file as it is and conversions.
    static func make(bitrate: Int, device: Device) -> [String: Any] {
        let video = ["h264", device.hevc ? "hevc" : nil, device.av1 ? "av1" : nil].compactMap(\.self).joined(separator: ",")
        let audio = "aac,mp3,ac3,eac3,flac,alac"
        let hevcRanges = ["SDR"] + (device.hdr ? ["HDR10", "HDR10Plus", "HLG", "DOVI", "DOVIWithHDR10", "DOVIWithHLG", "DOVIWithSDR", "DOVIWithHDR10Plus"] : [])
        var codecs: [[String: Any]] = [h264(maxLevel: 52)]
        if device.hevc {
            codecs.append(["Type": "Video", "Codec": "hevc", "Conditions": [
                condition("EqualsAny", "VideoProfile", "main|main 10"),
                condition("EqualsAny", "VideoRangeType", hevcRanges.joined(separator: "|")),
                condition("LessThanEqual", "VideoLevel", String(device.hevcLevel)),
                condition("NotEquals", "IsInterlaced", "true"),
                condition("LessThanEqual", "VideoFramerate", "60", required: true),
                condition("EqualsAny", "VideoCodecTag", "hvc1|dvh1"),
            ]])
        }
        // Another audio track than the file's first: Jellyfin copies the file into HLS with that one (the video
        // copied), rather than AVPlayer starting on the first.
        codecs.append(["Type": "VideoAudio", "Conditions": [condition("Equals", "IsSecondaryAudio", "false")]])
        return [
            "Name": "Mira for iPhone",
            "MaxStreamingBitrate": bitrate,
            "MaxStaticBitrate": bitrate,
            "MusicStreamingTranscodingBitrate": 384_000,
            "DirectPlayProfiles": [
                ["Container": "mp4,m4v", "Type": "Video", "VideoCodec": video, "AudioCodec": audio],
                ["Container": "mov", "Type": "Video", "VideoCodec": video, "AudioCodec": audio],
            ],
            // H.264 first: when Jellyfin must convert, it encodes the first codec, and H.264 costs a home server far
            // less; HEVC is still copied as it is where the iPhone plays it.
            "TranscodingProfiles": [[
                "Container": "mp4", "Type": "Video", "Protocol": "hls", "Context": "Streaming",
                "VideoCodec": device.hevc ? "h264,hevc" : "h264", "AudioCodec": "aac,ac3,eac3", "MaxAudioChannels": "6",
            ]],
            "ContainerProfiles": [[String: Any]](),
            "CodecProfiles": codecs,
            "SubtitleProfiles": subtitles,
        ]
    }

    /// The last try, after a stream refused even with its video converted: nothing sent as it is, 8-bit H.264 in SDR
    /// up to 1080p and stereo AAC (lastResortProfile of Mira web).
    static func lastResort(bitrate: Int) -> [String: Any] {
        [
            "Name": "Mira for iPhone (everything converted)",
            "MaxStreamingBitrate": bitrate,
            "MaxStaticBitrate": bitrate,
            "MusicStreamingTranscodingBitrate": 384_000,
            "DirectPlayProfiles": [[String: Any]](),
            "TranscodingProfiles": [[
                "Container": "mp4", "Type": "Video", "Protocol": "hls", "Context": "Streaming", "VideoCodec": "h264", "AudioCodec": "aac", "MaxAudioChannels": "2",
            ]],
            "ContainerProfiles": [[String: Any]](),
            "CodecProfiles": [h264(maxLevel: 51, maxWidth: 1920)],
            "SubtitleProfiles": subtitles,
        ]
    }
}
