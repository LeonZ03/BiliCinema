package com.leonz03.bilicinema;

import java.io.IOException;

/** Only these fixed messages cross the playback/network boundary into the UI. */
final class PlaybackFailure extends IOException {
    enum Reason { NEEDS_LOGIN, PERMISSION_DENIED, UNSUPPORTED, NETWORK_RETRYABLE, EXPIRED_SOURCE, INVALID_MEDIA, PLAYBACK_BLOCKED }
    final Reason reason;
    final String safeMessage;

    PlaybackFailure(Reason reason) {
        super(message(reason));
        this.reason = reason;
        this.safeMessage = message(reason);
    }

    String safeMessage() { return safeMessage; }
    boolean refreshRequired() { return reason == Reason.EXPIRED_SOURCE; }

    private static String message(Reason reason) {
        switch (reason) {
            case NEEDS_LOGIN: return "请先登录哔哩哔哩，再重新加载影片";
            case PERMISSION_DENIED: return "当前账号无权完整播放此影片，请检查会员、购买或地区权限";
            case UNSUPPORTED: return "此影片的编码、分段或 DRM 暂不支持原生播放，请使用哔哩哔哩官方客户端";
            case NETWORK_RETRYABLE: return "影片连接失败，请检查网络后重新加载";
            case EXPIRED_SOURCE: return "播放地址已失效，请重新加载影片";
            case INVALID_MEDIA: return "无法确认完整影片及分P信息，请重新选择影片";
            case PLAYBACK_BLOCKED: return "播放被系统暂时暂停，请结束其他音频或点击播放后重试";
            default: throw new IllegalArgumentException("Unknown playback failure");
        }
    }
}
