package com.leonz03.bilicinema;

import android.app.AlertDialog;
import android.app.Activity;
import android.app.Dialog;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.res.Configuration;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Rect;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.text.InputFilter;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.GestureDetector;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.accessibility.AccessibilityManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;

import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.PopupMenu;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/** Native presentation. Room and playback authority remain with the activity's existing owners. */
public final class CinemaUi {
    public interface Actions {
        void createRoom(String media);
        void joinRoom(String invitation);
        void restoreRoom();
        void retryRoomConnection();
        void cancelRoomConnection();
        void selectMedia(String media);
        void previewMedia(String media);
        void togglePlayback();
        void seek(double seconds);
        void rate(double value);
        void quality(int value);
        void toggleDanmaku();
        void subtitle(int index);
        void retryOverlayTracks();
        void retryPlayback();
        void login();
        void openOfficial();
        void checkUpdate();
        void invite();
        void leaveRoom();
        void setNickname(String nickname);
        void sendChat(String text);
        void fullscreenChanged(boolean fullscreen);
    }

    public enum PlaybackState {
        EMPTY, LOADING, READY, PLAYING, PAUSED, BUFFERING, CATCHING_UP, NEEDS_LOGIN,
        NEEDS_GESTURE, RETRYABLE_ERROR, NO_PERMISSION, DRM_PROTECTED, UNSUPPORTED, RECONNECTING, SYNCING, SYNC_FAILED
    }

    public static final class Member {
        public final String nickname;
        public final String status;
        public final boolean host;
        public final boolean self;
        public Member(String nickname, String status, boolean host, boolean self) {
            this.nickname = nickname; this.status = status; this.host = host; this.self = self;
        }
    }

    public static final class Model {
        public boolean inRoom, isHost, playing, busy, canRestore, loggedIn, canControl;
        public boolean connected = true;
        public String roomTitle = "一起看", mediaTitle = "尚未选择影片", status = "", detail = "";
        public String accountLabel = "B站登录", nickname = "影友", notice = "";
        public PlaybackState playbackState = PlaybackState.EMPTY;
        public double rate = 1;
        int preferredQuality;
        String qualityLabel = "清晰度";
        List<PlaybackSource.Quality> qualities = java.util.Collections.emptyList();
        List<BilibiliMediaResolver.Resolved.CatalogItem> catalog = java.util.Collections.emptyList();
        List<MediaOverlayTracks.Subtitle> subtitles = java.util.Collections.emptyList();
        String subtitleStatus = "";
        int subtitleIndex = -1;
        boolean danmakuEnabled;
        boolean danmakuAvailable;
        String danmakuStatus = "弹幕加载中";
        public List<Member> members = new ArrayList<>();
        RoomConnectionProgress roomConnection;
    }

    private int BG, SURFACE, BORDER, TEXT, MUTED, CYAN, GREEN, AMBER;
    private final Context context;
    private final Actions actions;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final FrameLayout root;
    private final List<ChatLine> history = new ArrayList<>();
    private List<MediaOverlayTracks.Danmaku> currentDanmaku = java.util.Collections.emptyList();
    private final CinemaInteraction.Previews previews = new CinemaInteraction.Previews(4);
    private Model model = new Model();
    private View playerView;
    private LinearLayout screen, roomHeader, chatPanel, chatLines, chatTabs, memberRows, mediaDetails, restoreSection;
    private FrameLayout video, playerHost, videoChrome, drawerLayer;
    private View playerTap, drawerScrim, topControls, bottomControls, playbackOverlay;
    private MediaOverlayView mediaOverlay;
    private TextView danmakuButton, subtitleButton, catalogButton;
    private ScrollView chatScroll;
    private BoundedScrollView metadataScroll;
    private EditText mediaInput, composer;
    private TextView headerTitle, mediaTitle, syncStatus, detail, notice, playerState;
    private TextView homeStatus, time, rateButton, fullscreenTitle;
    private Button qualityButton;
    private LinearLayout preview;
    private FrameLayout connectionLayer;
    private TextView connectionTitle, connectionSteps, connectionElapsed, connectionFailure;
    private Button connectionRetry, connectionCancel;
    private RoomConnectionProgress displayedConnection;
    private boolean connectionDismissed;
    private final Runnable connectionTick = new Runnable() {
        @Override public void run() {
            if (disposed || displayedConnection == null || connectionDismissed) return;
            updateConnectionElapsed();
            if (displayedConnection.outcome == RoomConnectionProgress.Outcome.RUNNING) main.postDelayed(this, 1000);
        }
    };
    private final Runnable dismissConnection = () -> {
        connectionDismissed = true;
        setConnectionVisible(false);
    };
    private Button create, open, restore, overlayAction;
    private ImageButton play, fullscreenButton, change, send, chatButton;
    private TextView memberCount, unreadBadge;
    private Dialog activeSheet;
    private LinearLayout mediaPreview;
    private TextView previewTitle, previewDuration;
    private android.widget.ImageView previewCover;
    private String previewInput = "", previewName = "";
    private double previewLength;
    private android.graphics.Bitmap coverBitmap;
    private final Runnable resolvePreview = this::requestPreview;
    private boolean builtDark, unread;
    private GestureDetector gestures;
    private SeekBar progress;
    private boolean fullscreen, drawerOpen, keyboardVisible, dragging, disposed;
    private String displayedNotice = "";
    private long noticeDeadline;
    private final Runnable hideNotice = () -> { if (notice != null) notice.setVisibility(View.GONE); };
    private boolean controlsVisible = true, builtInRoom, builtWatching, tablet, lastRenderedPlaying;
    private boolean overlayForeground = true;
    private double overlayRate = 1;
    private boolean overlayPlaying;
    private double position, duration;
    private String draft = "", mediaDraft = "", joinDraft = "", pendingChat = "";
    private String memberSignature = "";
    private int safeLeft, safeTop, safeRight, safeBottom, imeBottom;
    private Configuration layoutConfiguration;
    private final Runnable hideControls = () -> showControls(false);
    private final Runnable nextPreview = this::showNextPreview;

    public CinemaUi(Context context, Actions actions) {
        this.context = context; this.actions = actions;
        root = new FrameLayout(context);
        root.setBackgroundColor(BG);
        root.setFocusableInTouchMode(true);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                safeLeft = bars.left; safeTop = bars.top; safeRight = bars.right; safeBottom = bars.bottom;
                imeBottom = insets.getInsets(WindowInsets.Type.ime()).bottom;
                keyboardVisible = insets.isVisible(WindowInsets.Type.ime());
            } else {
                safeLeft = insets.getSystemWindowInsetLeft(); safeTop = insets.getSystemWindowInsetTop();
                safeRight = insets.getSystemWindowInsetRight();
                safeBottom = Math.min(insets.getSystemWindowInsetBottom(), insets.getStableInsetBottom());
            }
            applyInsets();
            return insets;
        });
        root.getViewTreeObserver().addOnGlobalLayoutListener(() -> {
            if (Build.VERSION.SDK_INT < 30) {
                Rect visible = new Rect(); root.getWindowVisibleDisplayFrame(visible);
                int occlusion = Math.max(0, root.getRootView().getHeight() - visible.bottom);
                boolean keyboard = occlusion > dp(160);
                int bottom = keyboard ? occlusion : 0;
                if (keyboard != keyboardVisible || imeBottom != bottom) {
                    keyboardVisible = keyboard; imeBottom = bottom; applyInsets();
                }
            }
        });
        rebuild();
    }

    public View root() { return root; }

    public void setPlayerView(View view) {
        if (playerView == view) return;
        detach(playerView); playerView = view;
        attachPlayer();
    }

    public void render(Model value) {
        if (disposed || value == null) return;
        boolean wasInRoom = builtInRoom;
        model = value;
        if (wasInRoom && !model.inRoom) {
            history.clear(); draft = ""; pendingChat = "";
            clearPreviews(); drawerOpen = false;
            if (fullscreen) setFullscreen(false);
        }
        boolean watching = model.inRoom || model.playbackState != PlaybackState.EMPTY;
        if (!watching && fullscreen) setFullscreen(false);
        if (builtInRoom != model.inRoom || builtWatching != watching || builtDark != (watching || CinemaTheme.isDark(context))) rebuild();
        bind();
    }

    public void updatePosition(double value, double length) {
        if (Double.isFinite(value)) position = Math.max(0, value);
        if (Double.isFinite(length)) duration = Math.max(0, length);
        if (time != null) time.setText(context.getString(R.string.playback_time, clock(position), clock(duration)));
        if (progress != null && !dragging) progress.setProgress(duration > 0 ? (int) Math.min(1000, position / duration * 1000) : 0);
        if (mediaOverlay != null) mediaOverlay.setPlayback(position, model.rate, overlayForeground && model.playing);
    }

    public void setOverlayPlayback(double value, double rate, boolean playing) {
        overlayRate = rate; overlayPlaying = playing;
        if (mediaOverlay != null) mediaOverlay.setPlayback(value, rate, playing && overlayForeground);
    }
    public void setOverlayForeground(boolean foreground) {
        overlayForeground = foreground;
        if (mediaOverlay != null) mediaOverlay.setPlayback(position, overlayRate, foreground && overlayPlaying);
    }

    public void updateTracks(List<MediaOverlayTracks.Subtitle> subtitles, List<MediaOverlayTracks.Danmaku> comments) {
        if (disposed) return;
        model.subtitles = new ArrayList<>(subtitles);
        currentDanmaku = new ArrayList<>(comments);
        if (model.subtitleIndex >= model.subtitles.size()) model.subtitleIndex = -1;
        if (mediaOverlay != null) mediaOverlay.setTracks(model.subtitles, comments);
        if (subtitleButton != null) subtitleButton.setEnabled(!model.subtitles.isEmpty());
    }

    public void appendChat(String nickname, String text, boolean own) {
        if (disposed) return;
        ChatLine line = new ChatLine(nickname, text, own);
        history.add(line);
        if (history.size() > 150) history.remove(0);
        if (chatLines != null) {
            if (chatLines.getChildCount() >= 150) chatLines.removeViewAt(0);
            addChatLine(line);
            scrollChat();
        }
        boolean visible = builtWatching && (!fullscreen || drawerOpen);
        if (!own && !visible) { unread = true; updateUnread(); }
        previews.offer(nickname, text, own, visible);
        if (preview == null) showNextPreview();
    }

    /** Called only after the transport accepted the pending message. */
    public void chatSent() {
        if (composer != null && composer.getText().toString().equals(pendingChat)) composer.setText("");
        if (draft.equals(pendingChat)) draft = "";
        pendingChat = "";
    }

    public boolean handleBack() {
        if (connectionLayer != null && connectionLayer.getVisibility() == View.VISIBLE) {
            if (keyboardVisible) { hideKeyboard(); return true; }
            if (displayedConnection != null && displayedConnection.outcome == RoomConnectionProgress.Outcome.COMPLETE) {
                dismissConnection.run(); return true;
            }
            return false;
        }
        switch (CinemaInteraction.back(keyboardVisible, drawerOpen, fullscreen)) {
            case KEYBOARD: hideKeyboard(); return true;
            case DRAWER: closeDrawer(); return true;
            case FULLSCREEN: setFullscreen(false); return true;
            default: return false;
        }
    }

    public void onConfigurationChanged(Configuration configuration) {
        // Keyboard/system-bar configuration events must not detach the player or
        // redefine fullscreen. Only an explicit fullscreen action owns that state.
        if (layoutConfiguration != null && layoutConfiguration.orientation == configuration.orientation
                && layoutConfiguration.screenWidthDp == configuration.screenWidthDp
                && layoutConfiguration.screenHeightDp == configuration.screenHeightDp
                && layoutConfiguration.smallestScreenWidthDp == configuration.smallestScreenWidthDp
                && layoutConfiguration.densityDpi == configuration.densityDpi
                && layoutConfiguration.fontScale == configuration.fontScale
                && layoutConfiguration.uiMode == configuration.uiMode) {
            root.requestApplyInsets(); return;
        }
        boolean restoreDrawer = drawerOpen;
        rebuild(); bind();
        if (restoreDrawer) openDrawer();
    }

    public void setFullscreen(boolean value) {
        if (fullscreen == value) return;
        fullscreen = value; drawerOpen = false; hideKeyboard(); clearPreviews();
        rebuild(); bind(); actions.fullscreenChanged(value);
    }

    public boolean isFullscreen() { return fullscreen; }

    public void dispose() {
        if (activeSheet != null) activeSheet.dismiss();
        disposed = true; main.removeCallbacksAndMessages(null); clearPreviews();
        detach(playerView); playerView = null;
    }

    private void rebuild() {
        layoutConfiguration = new Configuration(context.getResources().getConfiguration());
        if (activeSheet != null) activeSheet.dismiss();
        main.removeCallbacks(resolvePreview);
        if (composer != null) draft = composer.getText().toString();
        if (mediaInput != null) mediaDraft = mediaInput.getText().toString();
        detach(playerView); root.removeAllViews();
        clearPreviews(); memberSignature = "";
        builtInRoom = model.inRoom;
        builtWatching = model.inRoom || model.playbackState != PlaybackState.EMPTY;
        builtDark = builtWatching || CinemaTheme.isDark(context);
        CinemaTheme palette = CinemaTheme.of(context, builtWatching);
        BG = palette.background; SURFACE = palette.surface; BORDER = palette.border; TEXT = palette.text;
        MUTED = palette.muted; CYAN = palette.accent; GREEN = palette.success; AMBER = palette.warning;
        root.setBackgroundColor(BG);
        if (context instanceof Activity) CinemaTheme.applySystemBars((Activity) context, builtWatching);
        tablet = context.getResources().getConfiguration().smallestScreenWidthDp >= 600;
        screen = column(); screen.setFocusableInTouchMode(true);
        root.addView(screen, new FrameLayout.LayoutParams(-1, -1));
        mediaInput = null; composer = null; chatLines = null; memberRows = null; homeStatus = null;
        video = null; playerHost = null; drawerLayer = null; preview = null; mediaDetails = null; mediaOverlay = null;
        metadataScroll = null; chatTabs = null;
        create = null; open = null; restore = null; memberCount = null; unreadBadge = null; chatButton = null;
        if (!fullscreen) unread = false;
        if (!builtWatching) buildHome(); else buildRoom();
        buildConnectionProgress();
        applyInsets(); root.requestApplyInsets();
        attachPlayer();
        screen.requestFocus();
    }

    private void buildHome() {
        LinearLayout bar = row(); bar.setPadding(dp(20), dp(8), dp(12), dp(8));
        TextView brand = label("BiliCinema", 20, TEXT, true); bar.addView(brand, weight());
        bar.addView(icon(R.drawable.ic_account, "账号与设置", actions::login)); screen.addView(bar);
        ScrollView scroll = new ScrollView(context); scroll.setFillViewport(true);
        screen.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        int gutter = tablet ? Math.max(32, (context.getResources().getConfiguration().screenWidthDp - 560) / 2) : 20;
        LinearLayout page = column(); page.setPadding(dp(gutter), dp(32), dp(gutter), dp(24));
        scroll.addView(page, new ScrollView.LayoutParams(-1, -2));
        TextView linkLabel = label("影片", 16, TEXT, true); spacing(linkLabel, 0, 12); page.addView(linkLabel);
        LinearLayout inputRow = row();
        mediaInput = input("链接、分享文字或 BV 号", "影片链接"); mediaInput.setText(mediaDraft);
        inputRow.addView(clearableInput(mediaInput, "清除影片链接"), weight());
        inputRow.addView(icon(R.drawable.ic_paste, "粘贴影片链接", this::pasteMedia)); page.addView(inputRow);
        mediaPreview = row(); spacing(mediaPreview, 16, 0); mediaPreview.setPadding(dp(12), dp(12), dp(12), dp(12)); mediaPreview.setBackground(shape(SURFACE, 10));
        previewCover = new android.widget.ImageView(context); previewCover.setScaleType(android.widget.ImageView.ScaleType.CENTER_CROP);
        previewCover.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO); mediaPreview.addView(previewCover, new LinearLayout.LayoutParams(dp(88), dp(56)));
        LinearLayout info = column(); info.setPadding(dp(12), 0, 0, 0); previewTitle = label("", 14, TEXT, true); previewTitle.setMaxLines(2); previewTitle.setEllipsize(TextUtils.TruncateAt.END);
        previewDuration = label("", 13, MUTED, false); info.addView(previewTitle); info.addView(previewDuration); mediaPreview.addView(info, weight()); page.addView(mediaPreview);
        mediaPreview.setVisibility(View.GONE);
        if (mediaDraft.equals(previewInput)) showMediaPreview(previewInput, previewName, previewLength, coverBitmap);
        mediaInput.addTextChangedListener(new android.text.TextWatcher() {
            public void beforeTextChanged(CharSequence value, int start, int count, int after) { }
            public void onTextChanged(CharSequence value, int start, int before, int count) {
                main.removeCallbacks(resolvePreview); mediaPreview.setVisibility(View.GONE);
                if (value.length() == 0) actions.previewMedia(""); else main.postDelayed(resolvePreview, 700);
            }
            public void afterTextChanged(android.text.Editable value) { }
        });
        if (!mediaDraft.trim().isEmpty() && previewName.isEmpty()) main.postDelayed(resolvePreview, 700);
        LinearLayout buttons = row(); spacing(buttons, 20, 28);
        open = button("播放", true, () -> actions.selectMedia(mediaInput.getText().toString().trim()));
        buttons.addView(open, weight());
        create = button("创建房间", true, () -> actions.createRoom(mediaInput.getText().toString().trim()));
        create.setTextColor(CYAN); create.setBackground(touchBackground(SURFACE, 10));
        LinearLayout.LayoutParams second = weight(); second.setMarginStart(dp(12)); buttons.addView(create, second); page.addView(buttons);
        View divider = new View(context); divider.setBackgroundColor(BORDER); page.addView(divider, new LinearLayout.LayoutParams(-1, dp(1)));
        LinearLayout join = row(); spacing(join, 12, 12); join.setMinimumHeight(dp(56));
        ImageButton joinIcon = icon(R.drawable.ic_link, "加入房间", this::joinDialog); joinIcon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        join.addView(joinIcon); join.addView(label("加入房间", 16, TEXT, false), weight());
        join.addView(icon(R.drawable.ic_chevron_right, "打开加入房间", this::joinDialog));
        join.setBackground(touchBackground(Color.TRANSPARENT, 10)); join.setOnClickListener(view -> joinDialog()); page.addView(join);
        restoreSection = column();
        restore = button("继续上次的房间", false, actions::restoreRoom);
        spacing(restore, 12, 16); restoreSection.addView(restore);
        page.addView(restoreSection);
        homeStatus = label("", 14, MUTED, false); homeStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); page.addView(homeStatus);
    }

    private void buildRoom() {
        if (!fullscreen) {
            roomHeader = row(); roomHeader.setPadding(dp(8), dp(4), dp(8), dp(4));
            roomHeader.addView(icon(R.drawable.ic_back, model.inRoom ? "退出房间" : "返回首页", this::leaveDialog));
            headerTitle = label("", 18, TEXT, true); headerTitle.setSingleLine(true); headerTitle.setEllipsize(TextUtils.TruncateAt.END); roomHeader.addView(headerTitle, weight());
            if (model.inRoom) {
                LinearLayout members = row(); members.setMinimumHeight(dp(48)); members.setBackground(touchBackground(Color.TRANSPARENT, 10));
                ImageButton memberIcon = icon(R.drawable.ic_members, "查看成员", this::membersDialog); members.addView(memberIcon);
                memberCount = label("", 13, MUTED, false); memberCount.setPadding(0, 0, dp(8), 0); members.addView(memberCount);
                members.setOnClickListener(view -> membersDialog()); roomHeader.addView(members);
                roomHeader.addView(icon(R.drawable.ic_link, "复制房间邀请", actions::invite));
            }
            ImageButton more = icon(R.drawable.ic_more, "更多", () -> { }); more.setOnClickListener(this::roomMenu); roomHeader.addView(more);
            screen.addView(roomHeader);
        }
        boolean split = tablet && !fullscreen && context.getResources().getConfiguration().screenWidthDp >= 840;
        LinearLayout body = new RoomBody(context, split);
        body.setOrientation(split ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        screen.addView(body, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout watching = column();
        body.addView(watching, split ? new LinearLayout.LayoutParams(0, -1, 1) : new LinearLayout.LayoutParams(-1, fullscreen ? -1 : -2));
        buildVideo(watching, split);
        if (!fullscreen) buildMediaDetails(watching, split);
        buildChatPanel();
        if (!fullscreen && model.inRoom) {
            body.addView(chatPanel, split ? new LinearLayout.LayoutParams(dp(Math.max(320, Math.min(400,
                    context.getResources().getConfiguration().screenWidthDp / 3))), -1) : new LinearLayout.LayoutParams(-1, 0, 1));
        } else if (fullscreen && model.inRoom) {
            drawerLayer = new FrameLayout(context); drawerLayer.setVisibility(View.GONE);
            root.addView(drawerLayer, new FrameLayout.LayoutParams(-1, -1));
            drawerScrim = new View(context); drawerScrim.setBackgroundColor(0x77000000);
            drawerScrim.setOnTouchListener((view, event) -> {
                if (event.getActionMasked() == MotionEvent.ACTION_UP) view.performClick();
                return true;
            });
            drawerScrim.setContentDescription("关闭聊天和键盘"); drawerScrim.setOnClickListener(view -> closeDrawer());
            drawerLayer.addView(drawerScrim, new FrameLayout.LayoutParams(-1, -1));
            int available = context.getResources().getConfiguration().screenWidthDp;
            FrameLayout.LayoutParams params = available < 600
                ? new FrameLayout.LayoutParams(-1, dp(Math.min(360, context.getResources().getConfiguration().screenHeightDp / 2)), Gravity.BOTTOM)
                : new FrameLayout.LayoutParams(dp(Math.min(420, Math.max(280, Math.round(available * .32f)))), -1, Gravity.END);
            drawerLayer.addView(chatPanel, params);
            chatPanel.setClickable(true);
        }
    }

    private void buildVideo(LinearLayout watching, boolean split) {
        video = fullscreen || split ? new FrameLayout(context) : new VideoFrame(context);
        video.setBackgroundColor(Color.BLACK);
        watching.addView(video, split ? new LinearLayout.LayoutParams(-1, 0, 1)
                : new LinearLayout.LayoutParams(-1, fullscreen ? -1 : -2));
        playerHost = new FrameLayout(context); video.addView(playerHost, new FrameLayout.LayoutParams(-1, -1));
        playerTap = new View(context); playerTap.setContentDescription(fullscreen ? "显示播放控制；右侧区域打开聊天" : "显示播放控制");
        gestures = new GestureDetector(context, new GestureDetector.SimpleOnGestureListener() {
            @Override public boolean onDown(MotionEvent event) { return true; }
            @Override public boolean onSingleTapConfirmed(MotionEvent event) {
                if (disposed || video == null) return true;
                if (fullscreen && model.inRoom && CinemaInteraction.opensChat(event.getX(), event.getY(), video.getWidth(), video.getHeight(), dp(24),
                        topControls != null && topControls.getVisibility() == View.VISIBLE ? topControls.getHeight() : dp(24),
                        bottomControls.getVisibility() == View.VISIBLE ? bottomControls.getHeight() : dp(32), 0,
                        ViewConfiguration.get(context).getScaledTouchSlop())) openDrawer();
                else playerTap.performClick();
                return true;
            }
        });
        playerTap.setOnTouchListener((view, event) -> gestures.onTouchEvent(event)); playerTap.setOnClickListener(view -> showControls(!controlsVisible));
        video.addView(playerTap, new FrameLayout.LayoutParams(-1, -1));
        mediaOverlay = new MediaOverlayView(context); mediaOverlay.setTracks(model.subtitles, currentDanmaku);
        mediaOverlay.setSubtitleIndex(model.subtitleIndex); mediaOverlay.setDanmakuEnabled(model.danmakuEnabled);
        video.addView(mediaOverlay, new FrameLayout.LayoutParams(-1, -1));
        videoChrome = new FrameLayout(context); videoChrome.setClipChildren(false);
        video.addView(videoChrome, new FrameLayout.LayoutParams(-1, -1));
        if (fullscreen) {
            LinearLayout top = row(); top.setPadding(dp(8), dp(4), dp(8), dp(4)); top.setBackgroundColor(0xB3101216);
            top.addView(icon(R.drawable.ic_back, "退出全屏", () -> setFullscreen(false)));
            fullscreenTitle = label(model.mediaTitle, 18, TEXT, true); fullscreenTitle.setMaxLines(2); top.addView(fullscreenTitle, weight());
            if (model.inRoom) {
                FrameLayout entry = new FrameLayout(context);
                chatButton = icon(R.drawable.ic_chat, "打开聊天", this::openDrawer); entry.addView(chatButton, new FrameLayout.LayoutParams(dp(48), dp(48)));
                unreadBadge = label("", 10, TEXT, false); unreadBadge.setBackground(shape(CYAN, 4));
                FrameLayout.LayoutParams dot = new FrameLayout.LayoutParams(dp(7), dp(7), Gravity.END | Gravity.TOP); dot.topMargin = dp(9); dot.setMarginEnd(dp(9));
                entry.addView(unreadBadge, dot); top.addView(entry); updateUnread();
            }
            ImageButton more = icon(R.drawable.ic_more, "更多", () -> { }); more.setOnClickListener(this::roomMenu); top.addView(more);
            videoChrome.addView(top, new FrameLayout.LayoutParams(-1, -2, Gravity.TOP)); topControls = top;
        } else topControls = null;
        LinearLayout bottom = column(); bottom.setPadding(dp(8), 0, dp(8), dp(4)); bottom.setBackgroundColor(0xC0101216);
        progress = new SeekBar(context); progress.setMax(1000); progress.setMinimumHeight(dp(48));
        progress.setContentDescription("播放进度"); progress.setProgressTintList(ColorStateList.valueOf(CYAN)); progress.setThumbTintList(ColorStateList.valueOf(CYAN));
        progress.setProgressBackgroundTintList(ColorStateList.valueOf(0xFF666666)); progress.setSecondaryProgressTintList(ColorStateList.valueOf(0xFF444444));
        progress.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(SeekBar bar, int value, boolean fromUser) { if (fromUser && time != null) time.setText(context.getString(R.string.playback_time, clock(duration * value / 1000), clock(duration))); }
            public void onStartTrackingTouch(SeekBar bar) { dragging = true; main.removeCallbacks(hideControls); }
            public void onStopTrackingTouch(SeekBar bar) { dragging = false; if (duration > 0 && controlsAllowed()) actions.seek(duration * bar.getProgress() / 1000); scheduleControls(); }
        });
        LinearLayout timeline = row();
        timeline.addView(progress, new LinearLayout.LayoutParams(0, dp(48), 1));
        time = label("00:00 / 00:00", 13, TEXT, false); time.setSingleLine(true);
        time.setPadding(dp(8), 0, dp(4), 0); timeline.addView(time);
        bottom.addView(timeline);
        LinearLayout buttons = row();
        play = icon(R.drawable.ic_play, "播放", () -> { actions.togglePlayback(); scheduleControls(); }); buttons.addView(play);
        buttons.addView(new View(context), new LinearLayout.LayoutParams(0, 1, 1));
        qualityButton = textButton("清晰度", this::chooseQuality); qualityButton.setTextSize(14);
        qualityButton.setMinWidth(dp(64)); qualityButton.setMinimumWidth(dp(64)); qualityButton.setMaxWidth(dp(140));
        qualityButton.setSingleLine(true); qualityButton.setEllipsize(TextUtils.TruncateAt.END);
        qualityButton.setPadding(dp(8), dp(4), dp(8), dp(4)); buttons.addView(qualityButton);
        rateButton = label("1×", 15, TEXT, true); rateButton.setGravity(Gravity.CENTER); rateButton.setMinHeight(dp(48)); rateButton.setMinWidth(dp(48));
        rateButton.setBackground(touchBackground(Color.TRANSPARENT, 12)); rateButton.setContentDescription("播放倍速");
        rateButton.setOnClickListener(view -> chooseRate());
        LinearLayout.LayoutParams rateParams = new LinearLayout.LayoutParams(-2, -2); rateParams.setMargins(dp(8), 0, dp(8), 0);
        buttons.addView(rateButton, rateParams);
        danmakuButton = compactControl("弹幕", actions::toggleDanmaku);
        subtitleButton = compactControl("字幕", this::chooseSubtitle);
        catalogButton = compactControl("选集", this::chooseCatalog);
        if (fullscreen) {
            buttons.addView(danmakuButton, new LinearLayout.LayoutParams(dp(64), dp(48)));
            subtitleButton.setMaxWidth(dp(104)); subtitleButton.setSingleLine(true); subtitleButton.setEllipsize(TextUtils.TruncateAt.END);
            catalogButton.setMinWidth(dp(64));
            buttons.addView(subtitleButton, new LinearLayout.LayoutParams(-2, dp(48)));
            buttons.addView(catalogButton, new LinearLayout.LayoutParams(-2, dp(48)));
        }
        fullscreenButton = icon(fullscreen ? R.drawable.ic_fullscreen_exit : R.drawable.ic_fullscreen,
                fullscreen ? "退出全屏" : "全屏观看", () -> setFullscreen(!fullscreen)); buttons.addView(fullscreenButton);
        if (fullscreen) {
            HorizontalScrollView controlScroll = new HorizontalScrollView(context);
            controlScroll.setHorizontalScrollBarEnabled(false); controlScroll.setFillViewport(true);
            controlScroll.addView(buttons, new HorizontalScrollView.LayoutParams(-2, dp(48)));
            bottom.addView(controlScroll, new LinearLayout.LayoutParams(-1, dp(48)));
        } else bottom.addView(buttons);
        videoChrome.addView(bottom, new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM)); bottomControls = bottom;
        LinearLayout overlay = column(); overlay.setGravity(Gravity.CENTER); overlay.setPadding(dp(24), dp(12), dp(24), dp(12)); overlay.setBackgroundColor(0xB3101216);
        playerState = label("", 15, TEXT, false); playerState.setGravity(Gravity.CENTER); overlay.addView(playerState);
        overlayAction = button("重试播放", true, this::playbackAction);
        LinearLayout.LayoutParams actionParams = new LinearLayout.LayoutParams(-2, -2); actionParams.topMargin = dp(12); overlay.addView(overlayAction, actionParams);
        FrameLayout.LayoutParams stateParams = new FrameLayout.LayoutParams(-1, -2, Gravity.CENTER);
        stateParams.bottomMargin = dp(48); video.addView(overlay, stateParams); playbackOverlay = overlay;
        showControls(true);
    }

    private void buildMediaDetails(LinearLayout watching, boolean split) {
        LinearLayout metadata = column();
        metadataScroll = new BoundedScrollView(context);
        metadataScroll.setFillViewport(false);
        metadataScroll.addView(metadata, new ScrollView.LayoutParams(-1, -2));
        mediaDetails = column(); mediaDetails.setPadding(dp(20), dp(12), dp(20), dp(8));
        LinearLayout titleRow = row(); mediaTitle = label("", 16, TEXT, true); mediaTitle.setMaxLines(2); mediaTitle.setEllipsize(TextUtils.TruncateAt.END);
        mediaTitle.setMinHeight(dp(48)); mediaTitle.setGravity(Gravity.CENTER_VERTICAL);
        mediaTitle.setOnClickListener(view -> new AlertDialog.Builder(dialogContext()).setMessage(model.mediaTitle).setPositiveButton("关闭", null).show()); titleRow.addView(mediaTitle, weight());
        change = icon(R.drawable.ic_film, "选择影片", this::mediaDialog); titleRow.addView(change); mediaDetails.addView(titleRow);
        syncStatus = label("", 13, MUTED, false); syncStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); mediaDetails.addView(syncStatus);
        detail = label("", 14, MUTED, false); spacing(detail, 4, 0); mediaDetails.addView(detail);
        notice = label("", 14, AMBER, false); spacing(notice, 4, 0); mediaDetails.addView(notice);
        metadata.addView(mediaDetails, new LinearLayout.LayoutParams(-1, -2));
        if (!model.inRoom) { Button invite = textButton("创建房间", () -> actions.createRoom("")); spacing(invite, 12, 0); mediaDetails.addView(invite); }
        watching.addView(metadataScroll, new LinearLayout.LayoutParams(-1, -2));
    }

    private void buildChatPanel() {
        chatPanel = column(); chatPanel.setBackgroundColor(fullscreen ? SURFACE : BG); chatPanel.setPadding(dp(16), 0, dp(16), dp(8));
        if (Build.VERSION.SDK_INT >= 28) chatPanel.setAccessibilityPaneTitle(context.getString(R.string.chat_pane_title));
        else chatPanel.setContentDescription(context.getString(R.string.chat_pane_title));
        if (fullscreen) {
            LinearLayout tabs = row(); chatTabs = tabs;
            tabs.addView(label("聊天", 16, TEXT, true), weight());
            tabs.addView(icon(R.drawable.ic_members, "查看成员", this::membersDialog));
            tabs.addView(icon(R.drawable.ic_close, "关闭聊天", this::closeDrawer)); chatPanel.addView(tabs);
        } else {
            View divider = new View(context); divider.setBackgroundColor(BORDER); chatPanel.addView(divider, new LinearLayout.LayoutParams(-1, dp(1)));
        }
        chatScroll = new ScrollView(context); chatScroll.setFillViewport(true); chatPanel.addView(chatScroll, new LinearLayout.LayoutParams(-1, 0, 1));
        chatLines = column(); chatLines.setPadding(0, dp(4), 0, dp(12)); chatScroll.addView(chatLines);
        for (ChatLine line : history) addChatLine(line);
        LinearLayout compose = row(); compose.setGravity(Gravity.BOTTOM);
        composer = input("发送消息", "聊天内容，最多 500 字"); composer.setSingleLine(false); composer.setMaxLines(3);
        composer.setFilters(new InputFilter[]{new InputFilter.LengthFilter(500)}); composer.setText(draft);
        composer.setImeOptions(android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI);
        compose.addView(composer, weight());
        send = icon(R.drawable.ic_send, "发送消息", this::sendDraft); send.setImageTintList(ColorStateList.valueOf(CYAN));
        LinearLayout.LayoutParams sendParams = new LinearLayout.LayoutParams(dp(48), dp(52)); sendParams.setMarginStart(dp(8)); compose.addView(send, sendParams);
        chatPanel.addView(compose); composer.setEnabled(model.inRoom); send.setEnabled(model.inRoom && model.connected);
    }

    private void bind() {
        bindConnectionProgress();
        if (!builtWatching) {
            boolean available = !model.busy && (model.roomConnection == null || !model.roomConnection.needsResolution());
            create.setEnabled(available); open.setEnabled(available);
            restoreSection.setVisibility(model.canRestore ? View.VISIBLE : View.GONE); restore.setEnabled(!model.busy);
            homeStatus.setText(model.notice); homeStatus.setVisibility(TextUtils.isEmpty(model.notice) ? View.GONE : View.VISIBLE);
            return;
        }
        if (!fullscreen) {
            headerTitle.setText(model.inRoom ? model.roomTitle : "本机观看");
            if (memberCount != null) memberCount.setText(model.members.size() + "/5");
            mediaTitle.setText(model.mediaTitle); change.setEnabled(controlsAllowed() && !model.busy);
            change.setVisibility(controlsAllowed() ? View.VISIBLE : View.GONE);
            syncStatus.setText(stateLabel()); syncStatus.setTextColor(stateColor());
            detail.setText(model.status); detail.setVisibility(isFailure() && !TextUtils.isEmpty(model.status) && !model.status.equals(stateLabel()) ? View.VISIBLE : View.GONE);
            if (!displayedNotice.equals(model.notice)) {
                displayedNotice = model.notice; noticeDeadline = android.os.SystemClock.uptimeMillis() + 6000;
                main.removeCallbacks(hideNotice); main.postDelayed(hideNotice, 6000);
            }
            notice.setText(model.notice); notice.setVisibility(TextUtils.isEmpty(model.notice) || android.os.SystemClock.uptimeMillis() >= noticeDeadline ? View.GONE : View.VISIBLE);
        } else fullscreenTitle.setText(model.mediaTitle);
        play.setImageResource(model.playing ? R.drawable.ic_pause : R.drawable.ic_play);
        play.setContentDescription(model.playing ? "暂停" : "播放");
        play.setEnabled(controlsAllowed() && !model.busy && model.playbackState != PlaybackState.EMPTY);
        play.setAlpha(play.isEnabled() ? 1 : .4f);
        progress.setEnabled(controlsAllowed() && duration > 0 && !model.busy);
        rateButton.setText(String.format(Locale.ROOT, "%s×", model.rate)); rateButton.setEnabled(controlsAllowed()); rateButton.setAlpha(controlsAllowed() ? 1 : .4f);
        qualityButton.setText(model.qualityLabel);
        qualityButton.setContentDescription("选择清晰度，当前 " + model.qualityLabel);
        qualityButton.setEnabled(!model.busy && model.connected && !model.qualities.isEmpty());
        qualityButton.setAlpha(qualityButton.isEnabled() ? 1 : .4f);
        if (danmakuButton != null) {
            danmakuButton.setText(model.danmakuAvailable ? (model.danmakuEnabled ? "弹幕开" : "弹幕关") : model.danmakuStatus);
            danmakuButton.setEnabled((model.danmakuAvailable || "弹幕重试".equals(model.danmakuStatus)) && !model.busy);
        }
        if (subtitleButton != null) {
            MediaOverlayTracks.Subtitle selected = model.subtitleIndex >= 0 && model.subtitleIndex < model.subtitles.size()
                    ? model.subtitles.get(model.subtitleIndex) : null;
            subtitleButton.setText(selected == null ? "字幕" : selected.failed ? "字幕重试" : !selected.loaded ? "字幕加载中" : selected.label);
            subtitleButton.setContentDescription(model.subtitles.isEmpty() ? "此视频没有可选字幕" : "选择字幕");
            subtitleButton.setEnabled(!model.busy);
        }
        if (catalogButton != null) {
            catalogButton.setVisibility(fullscreen && (controlsAllowed() || model.inRoom) ? View.VISIBLE : View.GONE);
            catalogButton.setEnabled(!model.busy);
            if (model.catalog.size() < 2) catalogButton.setText("无选集"); else catalogButton.setText("选集");
        }
        if (mediaOverlay != null) {
            mediaOverlay.setTracks(model.subtitles, currentDanmaku);
            mediaOverlay.setSubtitleIndex(model.subtitleIndex); mediaOverlay.setDanmakuEnabled(model.danmakuEnabled);
        }
        String overlay = overlayLabel(); playerState.setText(overlay);
        playbackOverlay.setVisibility(overlay.isEmpty() ? View.GONE : View.VISIBLE);
        boolean actionable = model.playbackState == PlaybackState.RETRYABLE_ERROR || model.playbackState == PlaybackState.NEEDS_LOGIN
                || model.playbackState == PlaybackState.NEEDS_GESTURE || model.playbackState == PlaybackState.UNSUPPORTED
                || model.playbackState == PlaybackState.SYNC_FAILED
                || model.playbackState == PlaybackState.NO_PERMISSION || model.playbackState == PlaybackState.DRM_PROTECTED;
        overlayAction.setVisibility(actionable ? View.VISIBLE : View.GONE);
        overlayAction.setText(model.playbackState == PlaybackState.NEEDS_LOGIN ? "登录 B 站" : model.playbackState == PlaybackState.NO_PERMISSION ? "检查登录与权限" : model.playbackState == PlaybackState.DRM_PROTECTED ? "在B站打开" : model.playbackState == PlaybackState.NEEDS_GESTURE ? "启用本机播放" : model.playbackState == PlaybackState.UNSUPPORTED ? "重新选片" : model.playbackState == PlaybackState.SYNC_FAILED ? "重新同步" : "重试播放");
        overlayAction.setEnabled(!model.busy && (model.playbackState != PlaybackState.UNSUPPORTED || controlsAllowed()));
        composer.setEnabled(model.inRoom); send.setEnabled(model.inRoom && model.connected);
        updateMembers(); updatePosition(position, duration);
        if (!model.playing || !lastRenderedPlaying) showControls(true);
        lastRenderedPlaying = model.playing;
    }

    private void buildConnectionProgress() {
        connectionLayer = new FrameLayout(context);
        View scrim = new View(context); scrim.setBackgroundColor(0xAA101216); scrim.setClickable(true);
        connectionLayer.addView(scrim, new FrameLayout.LayoutParams(-1, -1));
        BoundedScrollView scroll = new BoundedScrollView(context); scroll.limitHeight = dp(520);
        LinearLayout card = card();
        connectionTitle = label("", 20, TEXT, true); card.addView(connectionTitle);
        connectionSteps = label("", 16, TEXT, false); spacing(connectionSteps, 16, 8); card.addView(connectionSteps);
        connectionSteps.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        connectionElapsed = label("", 14, MUTED, false); card.addView(connectionElapsed);
        connectionFailure = label("", 15, AMBER, false); spacing(connectionFailure, 12, 8); card.addView(connectionFailure);
        connectionRetry = button("重试", true, actions::retryRoomConnection); spacing(connectionRetry, 8, 8); card.addView(connectionRetry);
        connectionCancel = button("取消连接", false, () -> {
            if (displayedConnection != null && displayedConnection.outcome == RoomConnectionProgress.Outcome.COMPLETE) dismissConnection.run();
            else actions.cancelRoomConnection();
        }); card.addView(connectionCancel);
        scroll.addView(card, new ScrollView.LayoutParams(-1, -2));
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(dp(Math.min(560,
                context.getResources().getConfiguration().screenWidthDp)), -2, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        connectionLayer.addView(scroll, params);
        root.addView(connectionLayer, new FrameLayout.LayoutParams(-1, -1));
        connectionLayer.setVisibility(View.GONE);
    }

    private void bindConnectionProgress() {
        RoomConnectionProgress current = model.roomConnection;
        if (current == null || current.outcome == RoomConnectionProgress.Outcome.CANCELLED) {
            displayedConnection = current; setConnectionVisible(false);
            main.removeCallbacks(connectionTick); main.removeCallbacks(dismissConnection); return;
        }
        boolean newAttempt = displayedConnection == null || displayedConnection.startedMillis != current.startedMillis;
        boolean completed = current.outcome == RoomConnectionProgress.Outcome.COMPLETE
                && (newAttempt || displayedConnection.outcome != RoomConnectionProgress.Outcome.COMPLETE);
        if (newAttempt) { connectionDismissed = false; main.removeCallbacks(dismissConnection); hideKeyboard(); clearPreviews(); }
        displayedConnection = current;
        setConnectionVisible(!connectionDismissed);
        String title = current.operation == RoomConnectionProgress.Operation.CREATE ? "创建房间"
                : current.operation == RoomConnectionProgress.Operation.JOIN ? "加入房间"
                : current.operation == RoomConnectionProgress.Operation.RESTORE ? "恢复房间" : "重新连接房间";
        connectionTitle.setText(title);
        String request = current.joiningRequest ? "加入请求" : "创建请求";
        String[] stages = {"连接服务", request, "确认房间", "同步状态", "连接完成"};
        StringBuilder steps = new StringBuilder();
        for (int index = 0; index < stages.length; index++) {
            if (index > 0) steps.append('\n');
            steps.append(index < current.stage.ordinal() || current.outcome == RoomConnectionProgress.Outcome.COMPLETE ? "✓  "
                    : index == current.stage.ordinal() ? (current.outcome == RoomConnectionProgress.Outcome.FAILED ? "!  " : "正在") : "   ").append(stages[index]);
            if (index == current.stage.ordinal() && current.outcome == RoomConnectionProgress.Outcome.RUNNING) steps.append("…");
        }
        connectionSteps.setText(steps.toString());
        connectionFailure.setText(current.failure);
        connectionFailure.setVisibility(current.outcome == RoomConnectionProgress.Outcome.FAILED ? View.VISIBLE : View.GONE);
        connectionRetry.setVisibility(current.outcome == RoomConnectionProgress.Outcome.FAILED ? View.VISIBLE : View.GONE);
        connectionCancel.setText(current.outcome == RoomConnectionProgress.Outcome.COMPLETE ? "进入房间" : "取消连接");
        updateConnectionElapsed(); main.removeCallbacks(connectionTick);
        if (current.outcome == RoomConnectionProgress.Outcome.RUNNING) main.postDelayed(connectionTick, 1000);
        if (completed) main.postDelayed(dismissConnection, 1800);
    }

    private void updateConnectionElapsed() {
        if (displayedConnection == null || connectionElapsed == null) return;
        long seconds = displayedConnection.elapsedMillis(System.nanoTime() / 1_000_000) / 1000;
        connectionElapsed.setText(String.format(Locale.getDefault(), "已用时 %d 秒%s", seconds,
                displayedConnection.outcome == RoomConnectionProgress.Outcome.RUNNING ? " · 请稍候" : ""));
    }

    private void setConnectionVisible(boolean visible) {
        if (connectionLayer == null) return;
        connectionLayer.setVisibility(visible ? View.VISIBLE : View.GONE);
        screen.setImportantForAccessibility(visible || drawerOpen ? View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS : View.IMPORTANT_FOR_ACCESSIBILITY_AUTO);
        if (drawerLayer != null) drawerLayer.setImportantForAccessibility(visible ? View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS : View.IMPORTANT_FOR_ACCESSIBILITY_AUTO);
    }

    private void updateMembers() {
        StringBuilder signature = new StringBuilder();
        for (Member member : model.members) signature.append(member.nickname).append('\0').append(member.status).append(member.host).append(member.self);
        if (signature.toString().equals(memberSignature)) return;
        memberSignature = signature.toString();
        if (memberRows != null) {
            memberRows.removeAllViews();
            for (Member member : model.members) {
                LinearLayout item = column(); item.setPadding(dp(8), dp(8), dp(8), dp(8));
                TextView name = label(member.nickname + (member.self ? "（你）" : ""), 16, TEXT, false);
                name.setMaxLines(2); item.addView(name);
                item.addView(label((member.host ? "房主 · " : "") + member.status, 13, MUTED, false));
                memberRows.addView(item);
            }
            if (model.inRoom) memberRows.addView(textButton("邀请", actions::invite));
        }
    }

    private void addChatLine(ChatLine line) {
        View empty = chatLines.findViewWithTag("empty"); if (empty != null) chatLines.removeView(empty);
        int side = line.own ? Gravity.END : Gravity.START;
        LinearLayout message = column(); message.setPadding(0, dp(8), 0, dp(8)); message.setGravity(side);
        TextView name = label(line.nickname, 13, MUTED, false);
        name.setGravity(side); name.setTextAlignment(line.own ? View.TEXT_ALIGNMENT_VIEW_END : View.TEXT_ALIGNMENT_VIEW_START);
        message.addView(name, new LinearLayout.LayoutParams(-1, -2));
        TextView body = label(line.text, 16, TEXT, false); body.setTextIsSelectable(true);
        body.setPadding(dp(12), dp(8), dp(12), dp(8)); body.setBackground(shape(line.own ? 0xFF20364D : SURFACE, 10));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, -2); params.gravity = side; params.topMargin = dp(4); message.addView(body, params);
        chatLines.addView(message, new LinearLayout.LayoutParams(-1, -2));
    }

    private void sendDraft() {
        String value = composer.getText().toString();
        if (value.trim().isEmpty()) { composer.setError("写点内容再发送"); return; }
        pendingChat = value; draft = value; actions.sendChat(value);
    }

    private void openDrawer() {
        if (!fullscreen || !model.inRoom || drawerLayer == null) return;
        drawerOpen = true; unread = false; updateUnread(); clearPreviews(); drawerLayer.setVisibility(View.VISIBLE);
        chatPanel.setVisibility(View.VISIBLE); screen.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS);
        // The editor stays unfocused until the user explicitly taps it.
        chatPanel.setFocusableInTouchMode(true); chatPanel.requestFocus();
        main.removeCallbacks(hideControls); showControls(false); scrollChat();
    }

    private void closeDrawer() {
        draft = composer == null ? draft : composer.getText().toString();
        hideKeyboard(); drawerOpen = false;
        if (drawerLayer != null) drawerLayer.setVisibility(View.GONE);
        screen.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_AUTO); showControls(true);
    }

    private void showControls(boolean value) {
        if (bottomControls == null) return;
        AccessibilityManager accessibility = (AccessibilityManager) context.getSystemService(Context.ACCESSIBILITY_SERVICE);
        controlsVisible = !drawerOpen && (value || !model.playing || (accessibility != null && accessibility.isTouchExplorationEnabled()));
        bottomControls.setVisibility(controlsVisible ? View.VISIBLE : View.GONE);
        if (topControls != null) topControls.setVisibility(controlsVisible ? View.VISIBLE : View.GONE);
        if (mediaOverlay != null) mediaOverlay.setControlsInset(controlsVisible
                ? Math.max(dp(fullscreen ? 100 : 148), bottomControls.getMeasuredHeight()) : 0);
        if (controlsVisible) scheduleControls();
    }

    private void scheduleControls() {
        main.removeCallbacks(hideControls);
        if (model.playing && !dragging && !drawerOpen && controlsVisible) main.postDelayed(hideControls, 4000);
    }

    private void showNextPreview() {
        main.removeCallbacks(nextPreview);
        if (preview != null) { root.removeView(preview); preview = null; }
        CinemaInteraction.Preview item = previews.poll();
        if (disposed || item == null || !builtWatching || (fullscreen && drawerOpen)) return;
        preview = column();
        TextView sender = label(item.nickname, 15, TEXT, true);
        sender.setSingleLine(true); sender.setEllipsize(TextUtils.TruncateAt.END);
        TextView message = label(item.text, 15, TEXT, false);
        message.setMinLines(1); message.setMaxLines(2); message.setEllipsize(TextUtils.TruncateAt.END);
        sender.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        message.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        preview.addView(sender, new LinearLayout.LayoutParams(-1, -2));
        preview.addView(message, new LinearLayout.LayoutParams(-1, -2));
        preview.setPadding(dp(16), dp(12), dp(16), dp(12)); preview.setBackground(shape(SURFACE, 12));
        preview.setElevation(dp(8)); preview.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        preview.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES);
        preview.setContentDescription(item.accessibilityText);
        if (fullscreen) preview.setOnClickListener(view -> openDrawer());
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(dp(Math.min(300, Math.max(200,
                context.getResources().getConfiguration().screenWidthDp / 2))), -2, Gravity.END | Gravity.TOP);
        params.setMarginEnd(safeRight + dp(24)); params.topMargin = safeTop + dp(fullscreen ? 76 : 88);
        root.addView(preview, params);
        AccessibilityManager accessibility = (AccessibilityManager) context.getSystemService(Context.ACCESSIBILITY_SERVICE);
        int timeout = 4000;
        if (Build.VERSION.SDK_INT >= 29 && accessibility != null) timeout = accessibility.getRecommendedTimeoutMillis(timeout, AccessibilityManager.FLAG_CONTENT_TEXT);
        main.postDelayed(nextPreview, timeout);
    }

    private void clearPreviews() {
        main.removeCallbacks(nextPreview); previews.clear();
        if (preview != null) { root.removeView(preview); preview = null; }
    }

    private void applyInsets() {
        if (fullscreen) {
            // Video owns the entire, stable window. Only overlay controls/chat avoid
            // bars, cutouts and the keyboard; their visibility cannot resize the video.
            root.setPadding(0, 0, 0, 0);
            if (videoChrome != null) videoChrome.setPadding(safeLeft, safeTop, safeRight, safeBottom);
            if (drawerLayer != null) drawerLayer.setPadding(safeLeft, safeTop, safeRight, Math.max(safeBottom, imeBottom));
            if (connectionLayer != null) connectionLayer.setPadding(safeLeft, safeTop, safeRight, Math.max(safeBottom, imeBottom));
        } else root.setPadding(safeLeft, safeTop, safeRight, Math.max(safeBottom, imeBottom));
        if (composer != null) composer.setMaxLines(keyboardVisible ? 1 : 3);
        if (chatTabs != null) chatTabs.getChildAt(1).setVisibility(keyboardVisible ? View.GONE : View.VISIBLE);
        if (!fullscreen && video instanceof VideoFrame) {
            ((VideoFrame) video).limitHeight = keyboardVisible ? dp(120) : Integer.MAX_VALUE;
            video.requestLayout();
            if (metadataScroll != null) metadataScroll.setVisibility(keyboardVisible ? View.GONE : View.VISIBLE);
        }
    }

    private void hideKeyboard() {
        InputMethodManager keyboard = (InputMethodManager) context.getSystemService(Context.INPUT_METHOD_SERVICE);
        if (keyboard != null) keyboard.hideSoftInputFromWindow(root.getWindowToken(), 0);
        if (composer != null) composer.clearFocus(); screen.requestFocus(); keyboardVisible = false;
    }

    private void attachPlayer() {
        if (playerView == null || playerHost == null) return;
        detach(playerView); playerHost.addView(playerView, new FrameLayout.LayoutParams(-1, -1));
    }

    private void joinDialog() {
        EditText value = input("房间邀请链接或 22 位房间码", "房间邀请链接或房间码");
        value.setText(joinDraft);
        LinearLayout content = column(); content.addView(inputWithPaste(value));
        Dialog dialog = sheet("加入房间", content);
        Button join = button("加入", true, () -> {
            String text = value.getText().toString().trim();
            if (text.isEmpty()) { value.setError("请粘贴邀请链接或房间码"); return; }
            if (RoomInvitation.code(text) == null) { value.setError("邀请链接或房间码无效"); return; }
            actions.joinRoom(text); dialog.dismiss();
        }); spacing(join, 24, 0); content.addView(join);
        dialog.setOnDismissListener(ignored -> { joinDraft = value.getText().toString(); if (activeSheet == dialog) activeSheet = null; });
    }

    private void mediaDialog() {
        EditText value = input("粘贴 B 站链接或 BV 号", "影片链接"); value.setText(mediaDraft);
        LinearLayout content = column(); content.addView(inputWithPaste(value));
        Dialog dialog = sheet("选择影片", content);
        Button choose = button("播放", true, () -> {
            String text = value.getText().toString().trim();
            if (text.isEmpty()) { value.setError("请输入影片链接或编号"); return; }
            mediaDraft = text; actions.selectMedia(text); dialog.dismiss();
        }); spacing(choose, 24, 0); content.addView(choose);
        dialog.setOnDismissListener(ignored -> { mediaDraft = value.getText().toString(); if (activeSheet == dialog) activeSheet = null; });
    }

    public void receiveSharedMedia(String text) {
        mediaDraft = text;
        if (mediaInput != null) mediaInput.setText(text); else mediaDialog();
    }

    public void showMediaPreview(String input, String title, double length, android.graphics.Bitmap image) {
        if (disposed || builtWatching || mediaInput == null || !mediaInput.getText().toString().equals(input) || title.isEmpty()) return;
        if (!input.equals(previewInput)) coverBitmap = null;
        previewInput = input; previewName = title; previewLength = length;
        if (image != null) coverBitmap = image;
        previewTitle.setText(title); previewDuration.setText(length > 0 ? clock(length) : "");
        previewDuration.setVisibility(length > 0 ? View.VISIBLE : View.GONE);
        previewCover.setImageBitmap(coverBitmap); previewCover.setVisibility(coverBitmap == null ? View.GONE : View.VISIBLE);
        mediaPreview.setVisibility(View.VISIBLE);
    }
    public void previewUnavailable(String input) {
        if (!builtWatching && mediaInput != null && mediaInput.getText().toString().equals(input) && !input.equals(previewInput)) mediaPreview.setVisibility(View.GONE);
    }
    private void requestPreview() { if (mediaInput != null && !builtWatching) actions.previewMedia(mediaInput.getText().toString()); }

    private void nicknameDialog() {
        EditText value = input("你的房间昵称", "房间昵称"); value.setText(model.nickname);
        value.setFilters(new InputFilter[]{new InputFilter.LengthFilter(24)});
        AlertDialog dialog = new AlertDialog.Builder(dialogContext()).setTitle("修改房间昵称").setMessage("昵称与 B 站账号无关")
                .setView(dialogContent(value)).setNegativeButton("取消", null).setPositiveButton("保存", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            String text = value.getText().toString().trim();
            if (text.isEmpty()) { value.setError("请输入 1–24 字昵称"); return; }
            actions.setNickname(text); dialog.dismiss();
        })); dialog.show();
    }

    private void roomMenu(View anchor) {
        PopupMenu menu = new PopupMenu(dialogContext(), anchor);
        if (controlsAllowed()) menu.getMenu().add("选择影片").setOnMenuItemClickListener(item -> { mediaDialog(); return true; });
        if (!model.inRoom) {
            menu.getMenu().add("创建房间").setOnMenuItemClickListener(item -> { actions.createRoom(""); return true; });
            menu.getMenu().add("加入房间").setOnMenuItemClickListener(item -> { joinDialog(); return true; });
        }
        menu.getMenu().add("账号").setOnMenuItemClickListener(item -> { actions.login(); return true; });
        menu.getMenu().add("修改房间昵称").setOnMenuItemClickListener(item -> { nicknameDialog(); return true; });
        if (model.inRoom) menu.getMenu().add("退出房间").setOnMenuItemClickListener(item -> { leaveDialog(); return true; });
        menu.getMenu().add("检查更新").setOnMenuItemClickListener(item -> { actions.checkUpdate(); return true; }); menu.show();
    }

    private void chooseRate() {
        if (!controlsAllowed()) return;
        double[] values = {.5, .75, 1, 1.25, 1.5, 2};
        String[] labels = {"0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×"};
        LinearLayout options = column(); Dialog dialog = sheet("倍速", options);
        for (int i = 0; i < values.length; i++) {
            final int index = i;
            options.addView(choice(labels[i], values[i] == model.rate, () -> { actions.rate(values[index]); dialog.dismiss(); }));
        }
    }

    private void chooseCatalog() {
        if (!(controlsAllowed() || model.inRoom) || model.busy) return;
        main.removeCallbacks(hideControls);
        LinearLayout rows = column();
        Dialog dialog = sheet("选集 / 分P", rows);
        if (model.catalog.size() < 2) { rows.addView(label("此视频没有其他分P或剧集", 16, MUTED, false)); return; }
        for (BilibiliMediaResolver.Resolved.CatalogItem item : model.catalog) {
            if (model.inRoom && !model.isHost) {
                TextView title = label(item.title, 16, TEXT, false);
                title.setMinHeight(dp(52)); title.setGravity(Gravity.CENTER_VERTICAL);
                title.setPadding(dp(4), dp(4), dp(4), dp(4));
                rows.addView(title);
            } else {
                rows.addView(choice(item.title, false, () -> { actions.selectMedia(item.url); dialog.dismiss(); }));
            }
        }
        dialog.setOnDismissListener(ignored -> { if (activeSheet == dialog) activeSheet = null; scheduleControls(); });
    }

    private void chooseSubtitle() {
        if (model.busy) return;
        LinearLayout rows = column(); Dialog dialog = sheet("字幕", rows);
        if (model.subtitles.isEmpty()) {
            rows.addView(label(model.subtitleStatus.isEmpty() ? "此视频没有可选字幕" : model.subtitleStatus, 16, MUTED, false));
            if (model.subtitleStatus.contains("失败")) {
                Button retry = button("重试字幕列表", true, () -> { actions.retryOverlayTracks(); dialog.dismiss(); });
                spacing(retry, 16, 8); rows.addView(retry);
            }
            return;
        }
        rows.addView(choice("关闭字幕", model.subtitleIndex < 0, () -> { actions.subtitle(-1); dialog.dismiss(); }));
        for (int i = 0; i < model.subtitles.size(); i++) {
            final int index = i; MediaOverlayTracks.Subtitle track = model.subtitles.get(i);
            String label = track.failed ? track.label + " · 点击重试" : track.label;
            rows.addView(choice(label, model.subtitleIndex == i, () -> { actions.subtitle(index); dialog.dismiss(); }));
        }
    }

    private void chooseQuality() {
        if (model.busy || !model.connected || model.qualities.isEmpty()) return;
        List<PlaybackSource.Quality> options = new ArrayList<>(model.qualities);
        String[] labels = new String[options.size() + 1];
        labels[0] = "自动";
        int selected = model.preferredQuality == 0 ? 0 : -1;
        for (int i = 0; i < options.size(); i++) {
            labels[i + 1] = options.get(i).label;
            if (options.get(i).id == model.preferredQuality) selected = i + 1;
        }
        main.removeCallbacks(hideControls);
        LinearLayout rows = column(); Dialog dialog = sheet("清晰度", rows);
        for (int i = 0; i < labels.length; i++) {
            final int index = i;
            rows.addView(choice(labels[i], i == selected, () -> { actions.quality(index == 0 ? 0 : options.get(index - 1).id); dialog.dismiss(); }));
        }
        dialog.setOnDismissListener(ignored -> { if (activeSheet == dialog) activeSheet = null; scheduleControls(); });
    }

    private void leaveDialog() {
        if (!model.inRoom) { actions.leaveRoom(); return; }
        new AlertDialog.Builder(dialogContext()).setTitle("退出房间？").setMessage("可通过有效邀请再次加入")
                .setNegativeButton("继续观看", null).setPositiveButton("退出房间", (dialog, which) -> actions.leaveRoom()).show();
    }

    private void playbackAction() {
        if (model.playbackState == PlaybackState.NEEDS_LOGIN || model.playbackState == PlaybackState.NO_PERMISSION) actions.login();
        else if (model.playbackState == PlaybackState.DRM_PROTECTED) actions.openOfficial();
        else if (model.playbackState == PlaybackState.UNSUPPORTED) mediaDialog();
        else actions.retryPlayback();
    }

    private void pasteMedia() {
        pasteInto(mediaInput);
    }

    private void pasteInto(EditText field) {
        ClipboardManager clipboard = (ClipboardManager) context.getSystemService(Context.CLIPBOARD_SERVICE);
        if (clipboard != null && clipboard.hasPrimaryClip() && clipboard.getPrimaryClip() != null && clipboard.getPrimaryClip().getItemCount() > 0) {
            CharSequence text = clipboard.getPrimaryClip().getItemAt(0).coerceToText(context);
            if (text != null) { field.setText(text); field.setSelection(field.length()); }
        }
    }

    private String overlayLabel() {
        switch (model.playbackState) {
            case EMPTY: return "请选择影片";
            case LOADING: return "加载中…";
            case BUFFERING: return "缓冲中…";
            case CATCHING_UP: return "同步中…";
            case NEEDS_LOGIN: return "此影片需要本机登录 B 站";
            case NO_PERMISSION: return "当前账号暂无完整播放权限";
            case DRM_PROTECTED: return "此影片需要受保护播放";
            case NEEDS_GESTURE: return "点击启用本机播放";
            case SYNC_FAILED: return "暂未跟上房主";
            case RETRYABLE_ERROR: return "影片暂时加载失败";
            case UNSUPPORTED: return "本机暂不支持这部影片";
            case RECONNECTING: return "正在重新连接房间…";
            default: return "";
        }
    }

    private String stateLabel() {
        if (!model.connected && model.inRoom) return "房间连接已断开 · 正在重连";
        if (model.playbackState == PlaybackState.PLAYING) return model.inRoom ? (model.isHost ? "房主正在播放" : "跟随房主播放") : "正在播放";
        if (model.playbackState == PlaybackState.PAUSED) return model.inRoom && !model.isHost ? (model.playing ? "等待跟随播放…" : "房主已暂停") : "已暂停";
        if (model.playbackState == PlaybackState.READY) return "已就绪";
        if (model.playbackState == PlaybackState.SYNCING) return "同步中…";
        return nonempty(overlayLabel(), "房间已连接");
    }

    private int stateColor() {
        return model.playbackState == PlaybackState.READY || model.playbackState == PlaybackState.PLAYING || model.playbackState == PlaybackState.PAUSED ? GREEN : AMBER;
    }

    private boolean controlsAllowed() { return !model.inRoom || model.canControl; }
    private boolean isFailure() {
        return model.playbackState == PlaybackState.RETRYABLE_ERROR || model.playbackState == PlaybackState.NO_PERMISSION
                || model.playbackState == PlaybackState.UNSUPPORTED || model.playbackState == PlaybackState.DRM_PROTECTED
                || model.playbackState == PlaybackState.SYNC_FAILED;
    }
    private void updateUnread() {
        if (unreadBadge != null) unreadBadge.setVisibility(unread ? View.VISIBLE : View.GONE);
        if (chatButton != null) chatButton.setContentDescription(unread ? "打开聊天，有未读消息" : "打开聊天");
    }
    private Context dialogContext() {
        return new android.view.ContextThemeWrapper(context, builtDark ? android.R.style.Theme_Material_Dialog_Alert : android.R.style.Theme_Material_Light_Dialog_Alert);
    }
    private View inputWithPaste(EditText field) {
        LinearLayout line = row(); line.addView(clearableInput(field, "清除" + field.getContentDescription()), weight());
        line.addView(icon(R.drawable.ic_paste, "粘贴" + field.getContentDescription(), () -> pasteInto(field))); return line;
    }
    private Dialog sheet(String title, LinearLayout content) {
        if (activeSheet != null) activeSheet.dismiss();
        Dialog dialog = new Dialog(dialogContext()); dialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE);
        LinearLayout panel = column(); panel.setPadding(dp(20), dp(12), dp(20), dp(20)); panel.setBackground(shape(BG, 16));
        LinearLayout heading = row(); heading.addView(label(title, 20, TEXT, true), weight());
        heading.addView(icon(R.drawable.ic_close, "关闭" + title, dialog::dismiss)); panel.addView(heading);
        ScrollView scroll = new ScrollView(context); scroll.setFillViewport(false); scroll.addView(content);
        LinearLayout.LayoutParams bodyParams = new LinearLayout.LayoutParams(-1, -2); bodyParams.topMargin = dp(16); panel.addView(scroll, bodyParams);
        dialog.setContentView(panel); dialog.setOnDismissListener(ignored -> { if (activeSheet == dialog) activeSheet = null; });
        dialog.show(); activeSheet = dialog;
        android.view.Window window = dialog.getWindow();
        if (window != null) {
            window.setBackgroundDrawableResource(android.R.color.transparent);
            window.setGravity(Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
            window.setLayout(dp(Math.min(560, context.getResources().getConfiguration().screenWidthDp)), -2);
            window.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
            window.setDimAmount(.45f);
        }
        return dialog;
    }
    private View choice(String title, boolean selected, Runnable action) {
        LinearLayout row = row(); row.setPadding(dp(4), dp(4), 0, dp(4)); row.setMinimumHeight(dp(52));
        TextView text = label(title, 16, selected ? CYAN : TEXT, false); row.addView(text, weight());
        if (selected) { ImageButton check = icon(R.drawable.ic_check, "已选择", action); check.setImageTintList(ColorStateList.valueOf(CYAN)); row.addView(check); }
        row.setBackground(touchBackground(Color.TRANSPARENT, 8)); row.setOnClickListener(view -> action.run());
        row.setContentDescription(title + (selected ? "，已选择" : "")); row.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES); return row;
    }
    private TextView compactControl(String text, Runnable action) {
        TextView control = label(text, 13, TEXT, true); control.setGravity(Gravity.CENTER); control.setMinHeight(dp(48));
        control.setSingleLine(true); control.setEllipsize(TextUtils.TruncateAt.END);
        control.setMinWidth(dp(48)); control.setPadding(dp(4), 0, dp(4), 0);
        control.setBackground(touchBackground(Color.TRANSPARENT, 10)); control.setOnClickListener(view -> action.run());
        return control;
    }
    private void membersDialog() {
        if (!model.inRoom) return;
        LinearLayout rows = column(); Dialog dialog = sheet("成员 · " + model.members.size() + "/5", rows);
        memberRows = rows; memberSignature = ""; updateMembers();
        dialog.setOnDismissListener(ignored -> { memberRows = null; if (activeSheet == dialog) activeSheet = null; });
    }
    private void scrollChat() { if (chatScroll != null) chatScroll.post(() -> chatScroll.fullScroll(View.FOCUS_DOWN)); }
    private View dialogContent(EditText input) { LinearLayout wrap = column(); wrap.setPadding(dp(24), dp(8), dp(24), dp(8)); wrap.addView(clearableInput(input, "清除" + input.getContentDescription())); return wrap; }
    private LinearLayout column() { LinearLayout value = new LinearLayout(context); value.setOrientation(LinearLayout.VERTICAL); return value; }
    private LinearLayout row() { LinearLayout value = new LinearLayout(context); value.setOrientation(LinearLayout.HORIZONTAL); value.setGravity(Gravity.CENTER_VERTICAL); return value; }
    private LinearLayout card() { LinearLayout value = column(); value.setPadding(dp(16), dp(20), dp(16), dp(16)); value.setBackground(shape(SURFACE, 16)); return value; }
    private LinearLayout.LayoutParams weight() { return new LinearLayout.LayoutParams(0, -2, 1); }
    private int dp(float value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }
    private TextView label(String value, int size, int color, boolean bold) {
        TextView view = new TextView(context); view.setText(value); view.setTextSize(size); view.setTextColor(color); view.setIncludeFontPadding(true);
        view.setLineSpacing(dp(2), 1); if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD); return view;
    }
    private EditText input(String hint, String accessibleLabel) {
        EditText value = new EditText(context); value.setTextSize(16); value.setTextColor(TEXT); value.setHintTextColor(MUTED);
        value.setHint(hint); value.setContentDescription(accessibleLabel); value.setSingleLine(true); value.setMinHeight(dp(52));
        value.setPadding(dp(12), dp(8), dp(12), dp(8)); value.setBackground(touchBackground(SURFACE, 10));
        value.setSelectAllOnFocus(false); value.setImeOptions(android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI); return value;
    }
    private FrameLayout clearableInput(EditText input, String clearLabel) {
        FrameLayout field = new FrameLayout(context);
        input.setPadding(input.getPaddingLeft(), input.getPaddingTop(), dp(56), input.getPaddingBottom());
        field.addView(input, new FrameLayout.LayoutParams(-1, -2));
        ImageButton clear = new ImageButton(context);
        clear.setImageResource(R.drawable.ic_clear); clear.setImageTintList(ColorStateList.valueOf(MUTED));
        clear.setContentDescription(clearLabel); clear.setBackground(touchBackground(Color.TRANSPARENT, 24));
        clear.setPadding(dp(14), dp(14), dp(14), dp(14)); clear.setMinimumWidth(dp(48)); clear.setMinimumHeight(dp(48));
        FrameLayout.LayoutParams clearParams = new FrameLayout.LayoutParams(dp(48), dp(48), Gravity.END | Gravity.CENTER_VERTICAL);
        field.addView(clear, clearParams);
        clear.setVisibility(input.length() == 0 ? View.GONE : View.VISIBLE);
        input.addTextChangedListener(new android.text.TextWatcher() {
            @Override public void beforeTextChanged(CharSequence text, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence text, int start, int before, int count) {
                clear.setVisibility(text.length() == 0 ? View.GONE : View.VISIBLE);
            }
            @Override public void afterTextChanged(android.text.Editable text) { }
        });
        clear.setOnClickListener(view -> {
            input.setText(""); input.requestFocus();
            InputMethodManager keyboard = (InputMethodManager) context.getSystemService(Context.INPUT_METHOD_SERVICE);
            if (keyboard != null) keyboard.showSoftInput(input, InputMethodManager.SHOW_IMPLICIT);
        });
        return field;
    }
    private Button button(String text, boolean primary, Runnable action) {
        Button button = new Button(context); button.setText(text); button.setAllCaps(false); button.setTextSize(16);
        button.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled}, new int[]{}},
                new int[]{MUTED, primary ? (builtDark ? Color.rgb(15, 15, 15) : Color.WHITE) : TEXT})); button.setMinHeight(dp(48)); button.setMinimumHeight(dp(48));
        button.setPadding(dp(16), dp(8), dp(16), dp(8)); button.setBackground(touchBackground(primary ? CYAN : SURFACE, 10));
        button.setOnClickListener(view -> action.run()); button.setStateListAnimator(null); return button;
    }
    private Button textButton(String text, Runnable action) {
        Button value = button(text, false, action); value.setTextColor(CYAN); value.setBackground(touchBackground(Color.TRANSPARENT, 12)); return value;
    }
    private ImageButton icon(int drawable, String description, Runnable action) {
        ImageButton value = new ImageButton(context); value.setImageResource(drawable); value.setImageTintList(ColorStateList.valueOf(TEXT));
        value.setContentDescription(description); if (Build.VERSION.SDK_INT >= 26) value.setTooltipText(description);
        value.setBackground(touchBackground(Color.TRANSPARENT, 24)); value.setPadding(dp(12), dp(12), dp(12), dp(12));
        value.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48))); value.setOnClickListener(view -> action.run()); return value;
    }
    private GradientDrawable shape(int color, int radius) { GradientDrawable value = new GradientDrawable(); value.setColor(color); value.setCornerRadius(dp(radius)); return value; }
    private RippleDrawable touchBackground(int color, int radius) { return new RippleDrawable(ColorStateList.valueOf(0x22065FD4), shape(color, radius), shape(Color.WHITE, radius)); }
    private void spacing(View value, int top, int bottom) { LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2); params.topMargin = dp(top); params.bottomMargin = dp(bottom); value.setLayoutParams(params); }
    private static void detach(View value) { if (value != null && value.getParent() instanceof ViewGroup) ((ViewGroup) value.getParent()).removeView(value); }
    private static String nonempty(String value, String fallback) { return value == null || value.trim().isEmpty() ? fallback : value; }
    private static String clock(double seconds) { long value = Math.max(0, (long) seconds); return value >= 3600 ? String.format(Locale.ROOT, "%d:%02d:%02d", value / 3600, value / 60 % 60, value % 60) : String.format(Locale.ROOT, "%02d:%02d", value / 60, value % 60); }

    private static final class ChatLine {
        final String nickname, text; final boolean own;
        ChatLine(String nickname, String text, boolean own) { this.nickname = nickname; this.text = text; this.own = own; }
    }

    private static final class VideoFrame extends FrameLayout {
        int limitHeight = Integer.MAX_VALUE;
        VideoFrame(Context context) { super(context); }
        @Override protected void onMeasure(int widthSpec, int heightSpec) {
            int width = View.MeasureSpec.getSize(widthSpec);
            super.onMeasure(widthSpec, View.MeasureSpec.makeMeasureSpec(Math.min(limitHeight, Math.round(width * 9f / 16f)), View.MeasureSpec.EXACTLY));
        }
    }

    /** Measures the existing panes together so optional metadata cannot consume their usable height. */
    private final class RoomBody extends LinearLayout {
        private final boolean split;
        RoomBody(Context context, boolean split) { super(context); this.split = split; }
        @Override protected void onMeasure(int widthSpec, int heightSpec) {
            if (!fullscreen && metadataScroll != null && chatPanel != null) {
                int available = View.MeasureSpec.getSize(heightSpec);
                if (split) {
                    metadataScroll.limitHeight = Math.max(0, available - dp(160));
                } else if (video instanceof VideoFrame) {
                    int width = View.MeasureSpec.getSize(widthSpec);
                    int fixedChat = chatPanel.getPaddingTop() + chatPanel.getPaddingBottom();
                    int contentWidth = Math.max(0, width - chatPanel.getPaddingLeft() - chatPanel.getPaddingRight());
                    for (int index = 0; index < chatPanel.getChildCount(); index++) {
                        View child = chatPanel.getChildAt(index);
                        if (child == chatScroll || child.getVisibility() == View.GONE) continue;
                        child.measure(View.MeasureSpec.makeMeasureSpec(contentWidth, View.MeasureSpec.EXACTLY),
                                View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED));
                        fixedChat += child.getMeasuredHeight();
                    }
                    int minimumVideo = Math.min(dp(keyboardVisible ? 64 : 96), Math.max(0, available - fixedChat));
                    int metadataMinimum = keyboardVisible ? 0 : Math.min(dp(48), Math.max(0, available - fixedChat - minimumVideo));
                    int chatBudget = Math.min(Math.max(0, available - minimumVideo - metadataMinimum),
                            fixedChat + (keyboardVisible ? dp(32) : dp(96)));
                    int desiredVideo = keyboardVisible ? dp(120) : Math.round(width * 9f / 16f);
                    int videoBudget = Math.min(desiredVideo, Math.max(minimumVideo, available - chatBudget - metadataMinimum));
                    ((VideoFrame) video).limitHeight = videoBudget;
                    metadataScroll.limitHeight = Math.min(dp(240), Math.max(0, available - chatBudget - videoBudget));
                }
            }
            super.onMeasure(widthSpec, heightSpec);
        }
    }

    private static final class BoundedScrollView extends ScrollView {
        int limitHeight = Integer.MAX_VALUE;
        BoundedScrollView(Context context) { super(context); }
        @Override protected void onMeasure(int widthSpec, int heightSpec) {
            int maximum = View.MeasureSpec.getMode(heightSpec) == View.MeasureSpec.UNSPECIFIED
                    ? limitHeight : Math.min(limitHeight, View.MeasureSpec.getSize(heightSpec));
            super.onMeasure(widthSpec, View.MeasureSpec.makeMeasureSpec(maximum, View.MeasureSpec.AT_MOST));
        }
    }
}
