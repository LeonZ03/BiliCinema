package com.leonz03.bilicinema;

import android.app.AlertDialog;
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
        void selectMedia(String media);
        void togglePlayback();
        void seek(double seconds);
        void rate(double value);
        void retryPlayback();
        void login();
        void checkUpdate();
        void invite();
        void leaveRoom();
        void setNickname(String nickname);
        void sendChat(String text);
        void fullscreenChanged(boolean fullscreen);
    }

    public enum PlaybackState {
        EMPTY, LOADING, READY, PLAYING, PAUSED, BUFFERING, CATCHING_UP, NEEDS_LOGIN,
        NEEDS_GESTURE, RETRYABLE_ERROR, UNSUPPORTED, RECONNECTING
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
        public List<Member> members = new ArrayList<>();
    }

    private static final int BG = Color.rgb(16, 18, 22), SURFACE = Color.rgb(26, 30, 37);
    private static final int BORDER = Color.rgb(48, 54, 66), TEXT = Color.rgb(244, 246, 250);
    private static final int MUTED = Color.rgb(168, 178, 195), CYAN = Color.rgb(100, 201, 223);
    private static final int GREEN = Color.rgb(102, 214, 175), AMBER = Color.rgb(244, 196, 120);
    private final Context context;
    private final Actions actions;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final FrameLayout root;
    private final List<ChatLine> history = new ArrayList<>();
    private final CinemaInteraction.Previews previews = new CinemaInteraction.Previews(4);
    private Model model = new Model();
    private View playerView;
    private LinearLayout screen, roomHeader, chatPanel, chatLines, chatTabs, memberRows, mediaDetails, restoreSection;
    private FrameLayout video, playerHost, videoChrome, drawerLayer;
    private View playerTap, drawerScrim, topControls, bottomControls, playbackOverlay, memberStrip;
    private ScrollView chatScroll;
    private BoundedScrollView metadataScroll;
    private EditText mediaInput, composer;
    private TextView headerTitle, headerSubtitle, mediaTitle, syncStatus, detail, notice, playerState;
    private TextView homeStatus, time, rateButton, fullscreenTitle;
    private LinearLayout preview;
    private Button create, open, restore, change, overlayAction, send;
    private ImageButton play, fullscreenButton;
    private SeekBar progress;
    private boolean fullscreen, drawerOpen, membersSelected, keyboardVisible, dragging, disposed;
    private boolean controlsVisible = true, builtInRoom, builtWatching, tablet, lastRenderedPlaying;
    private double position, duration;
    private String draft = "", mediaDraft = "", pendingChat = "";
    private String memberSignature = "";
    private int safeLeft, safeTop, safeRight, safeBottom, imeBottom;
    private float touchX, touchY, movement;
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
                safeRight = insets.getSystemWindowInsetRight(); safeBottom = insets.getSystemWindowInsetBottom();
            }
            applyInsets();
            return insets;
        });
        root.getViewTreeObserver().addOnGlobalLayoutListener(() -> {
            if (Build.VERSION.SDK_INT < 30) {
                Rect visible = new Rect(); root.getWindowVisibleDisplayFrame(visible);
                boolean keyboard = root.getRootView().getHeight() - visible.bottom > dp(160);
                if (keyboard != keyboardVisible) { keyboardVisible = keyboard; applyInsets(); }
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
        Configuration configuration = context.getResources().getConfiguration();
        if (watching && !builtWatching && configuration.smallestScreenWidthDp < 600
                && configuration.orientation == Configuration.ORIENTATION_LANDSCAPE && !fullscreen) {
            fullscreen = true; actions.fullscreenChanged(true);
        }
        if (builtInRoom != model.inRoom || builtWatching != watching) rebuild();
        bind();
    }

    public void updatePosition(double value, double length) {
        if (Double.isFinite(value)) position = Math.max(0, value);
        if (Double.isFinite(length)) duration = Math.max(0, length);
        if (time != null) time.setText(context.getString(R.string.playback_time, clock(position), clock(duration)));
        if (progress != null && !dragging) progress.setProgress(duration > 0 ? (int) Math.min(1000, position / duration * 1000) : 0);
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
        boolean visible = builtWatching && (!fullscreen || drawerOpen) && !membersSelected;
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
        switch (CinemaInteraction.back(keyboardVisible, drawerOpen, fullscreen)) {
            case KEYBOARD: hideKeyboard(); return true;
            case DRAWER: closeDrawer(); return true;
            case FULLSCREEN: setFullscreen(false); return true;
            default: return false;
        }
    }

    public void onConfigurationChanged(Configuration configuration) {
        drawerOpen = false;
        boolean phoneLandscape = configuration.smallestScreenWidthDp < 600
                && configuration.orientation == Configuration.ORIENTATION_LANDSCAPE;
        if (builtWatching && phoneLandscape != fullscreen && configuration.smallestScreenWidthDp < 600) {
            fullscreen = phoneLandscape;
            actions.fullscreenChanged(fullscreen);
        }
        rebuild(); bind();
    }

    public void setFullscreen(boolean value) {
        if (fullscreen == value) return;
        fullscreen = value; drawerOpen = false; hideKeyboard(); clearPreviews();
        rebuild(); bind(); actions.fullscreenChanged(value);
    }

    public boolean isFullscreen() { return fullscreen; }

    public void dispose() {
        disposed = true; main.removeCallbacksAndMessages(null); clearPreviews();
        detach(playerView); playerView = null;
    }

    private void rebuild() {
        if (composer != null) draft = composer.getText().toString();
        if (mediaInput != null) mediaDraft = mediaInput.getText().toString();
        detach(playerView); root.removeAllViews();
        clearPreviews(); memberSignature = "";
        builtInRoom = model.inRoom;
        builtWatching = model.inRoom || model.playbackState != PlaybackState.EMPTY;
        tablet = context.getResources().getConfiguration().smallestScreenWidthDp >= 600;
        screen = column(); screen.setFocusableInTouchMode(true);
        root.addView(screen, new FrameLayout.LayoutParams(-1, -1));
        mediaInput = null; composer = null; chatLines = null; memberRows = null; homeStatus = null;
        video = null; playerHost = null; drawerLayer = null; preview = null; memberStrip = null; mediaDetails = null;
        metadataScroll = null; chatTabs = null;
        create = null; open = null; restore = null;
        if (!builtWatching) buildHome(); else buildRoom();
        applyInsets(); root.requestApplyInsets();
        attachPlayer();
        screen.requestFocus();
    }

    private void buildHome() {
        LinearLayout bar = row(); bar.setPadding(dp(20), dp(8), dp(12), dp(8));
        TextView brand = label("BiliCinema", 24, TEXT, true); bar.addView(brand, weight());
        ImageButton more = icon(R.drawable.ic_more, "账号与更多", () -> { });
        more.setOnClickListener(view -> accountMenu(view)); bar.addView(more); screen.addView(bar);
        ScrollView scroll = new ScrollView(context); scroll.setFillViewport(true);
        screen.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout page = column(); page.setPadding(dp(tablet ? 48 : 20), dp(32), dp(tablet ? 48 : 20), dp(24));
        scroll.addView(page, new ScrollView.LayoutParams(-1, -2));
        page.addView(label("今晚，一起看。", 32, TEXT, true));
        TextView subtitle = label("和朋友开一间放映室", 18, MUTED, false); spacing(subtitle, 8, 32); page.addView(subtitle);
        LinearLayout card = card();
        card.addView(label("想看什么？", 22, TEXT, true));
        TextView linkLabel = label("影片链接", 14, MUTED, false); spacing(linkLabel, 20, 4); card.addView(linkLabel);
        LinearLayout inputRow = row();
        mediaInput = input("粘贴 B 站链接或 BV 号", "影片链接"); mediaInput.setText(mediaDraft);
        inputRow.addView(mediaInput, weight());
        Button paste = textButton("粘贴", this::pasteMedia); inputRow.addView(paste); card.addView(inputRow);
        create = button("创建房间", true, () -> actions.createRoom(mediaInput.getText().toString().trim()));
        spacing(create, 20, 8); card.addView(create);
        card.addView(button("加入房间", false, this::joinDialog));
        open = textButton("先单独观看", () -> actions.selectMedia(mediaInput.getText().toString().trim()));
        open.setBackground(new RippleDrawable(ColorStateList.valueOf(0x4464C9DF), null, shape(Color.WHITE, 12)));
        spacing(open, 8, 0); card.addView(open); page.addView(card);
        restoreSection = column();
        restore = button("继续上次的房间", false, actions::restoreRoom);
        spacing(restore, 16, 0); restoreSection.addView(restore);
        TextView local = label("仅在房间仍有效时恢复", 14, MUTED, false); spacing(local, 8, 16); restoreSection.addView(local);
        page.addView(restoreSection);
        homeStatus = label("", 15, MUTED, false); homeStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); page.addView(homeStatus);
        TextView footer = label("最多 5 人 · 各自账号播放", 14, MUTED, false); footer.setGravity(Gravity.CENTER); spacing(footer, 32, 8); page.addView(footer);
    }

    private void buildRoom() {
        if (!fullscreen) {
            roomHeader = row(); roomHeader.setPadding(dp(8), dp(4), dp(8), dp(4));
            roomHeader.addView(icon(R.drawable.ic_back, model.inRoom ? "退出房间" : "返回首页", this::leaveDialog));
            LinearLayout titles = column(); headerTitle = label("", 18, TEXT, true); titles.addView(headerTitle);
            headerSubtitle = label("", 13, MUTED, false); titles.addView(headerSubtitle); roomHeader.addView(titles, weight());
            if (model.inRoom) roomHeader.addView(icon(R.drawable.ic_link, "复制房间邀请", actions::invite));
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
        if (!fullscreen) {
            body.addView(chatPanel, split ? new LinearLayout.LayoutParams(dp(Math.max(320, Math.min(400,
                    context.getResources().getConfiguration().screenWidthDp / 3))), -1) : new LinearLayout.LayoutParams(-1, 0, 1));
        } else {
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
            FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(dp(Math.min(380, Math.max(280, available * 2 / 5))), -1, Gravity.END);
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
        playerTap.setOnTouchListener(this::videoTouch); playerTap.setOnClickListener(view -> showControls(!controlsVisible));
        video.addView(playerTap, new FrameLayout.LayoutParams(-1, -1));
        videoChrome = new FrameLayout(context); videoChrome.setClipChildren(false);
        video.addView(videoChrome, new FrameLayout.LayoutParams(-1, -1));
        if (fullscreen) {
            LinearLayout top = row(); top.setPadding(dp(8), dp(4), dp(8), dp(4)); top.setBackgroundColor(0xB3101216);
            top.addView(icon(R.drawable.ic_back, "退出全屏", () -> setFullscreen(false)));
            fullscreenTitle = label(model.mediaTitle, 18, TEXT, true); fullscreenTitle.setMaxLines(2); top.addView(fullscreenTitle, weight());
            if (model.inRoom) top.addView(icon(R.drawable.ic_chat, "打开聊天", this::openDrawer));
            ImageButton more = icon(R.drawable.ic_more, "更多", () -> { }); more.setOnClickListener(this::roomMenu); top.addView(more);
            videoChrome.addView(top, new FrameLayout.LayoutParams(-1, -2, Gravity.TOP)); topControls = top;
        } else topControls = null;
        LinearLayout bottom = column(); bottom.setPadding(dp(8), 0, dp(8), dp(4)); bottom.setBackgroundColor(0xC0101216);
        progress = new SeekBar(context); progress.setMax(1000); progress.setMinimumHeight(dp(48));
        progress.setContentDescription("播放进度"); progress.setProgressTintList(ColorStateList.valueOf(CYAN)); progress.setThumbTintList(ColorStateList.valueOf(CYAN));
        progress.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(SeekBar bar, int value, boolean fromUser) { if (fromUser && time != null) time.setText(context.getString(R.string.playback_time, clock(duration * value / 1000), clock(duration))); }
            public void onStartTrackingTouch(SeekBar bar) { dragging = true; main.removeCallbacks(hideControls); }
            public void onStopTrackingTouch(SeekBar bar) { dragging = false; if (duration > 0 && controlsAllowed()) actions.seek(duration * bar.getProgress() / 1000); scheduleControls(); }
        });
        bottom.addView(progress, new LinearLayout.LayoutParams(-1, dp(48)));
        LinearLayout buttons = row();
        play = icon(R.drawable.ic_play, "播放", () -> { actions.togglePlayback(); scheduleControls(); }); buttons.addView(play);
        time = label("00:00 / 00:00", 13, TEXT, false); buttons.addView(time, weight());
        rateButton = label("1×", 15, TEXT, true); rateButton.setGravity(Gravity.CENTER); rateButton.setMinHeight(dp(48)); rateButton.setMinWidth(dp(48));
        rateButton.setBackground(touchBackground(Color.TRANSPARENT, 12)); rateButton.setContentDescription("播放倍速");
        rateButton.setOnClickListener(view -> chooseRate()); buttons.addView(rateButton);
        fullscreenButton = icon(fullscreen ? R.drawable.ic_fullscreen_exit : R.drawable.ic_fullscreen,
                fullscreen ? "退出全屏" : "全屏观看", () -> setFullscreen(!fullscreen)); buttons.addView(fullscreenButton);
        bottom.addView(buttons); videoChrome.addView(bottom, new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM)); bottomControls = bottom;
        LinearLayout overlay = column(); overlay.setGravity(Gravity.CENTER); overlay.setPadding(dp(24), dp(12), dp(24), dp(12)); overlay.setBackgroundColor(0xB3101216);
        playerState = label("", 18, TEXT, true); playerState.setGravity(Gravity.CENTER); overlay.addView(playerState);
        overlayAction = button("重试播放", true, this::playbackAction); spacing(overlayAction, 12, 0); overlay.addView(overlayAction);
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
        LinearLayout titleRow = row(); mediaTitle = label("", 20, TEXT, true); titleRow.addView(mediaTitle, weight());
        change = textButton("换片", this::mediaDialog); titleRow.addView(change); mediaDetails.addView(titleRow);
        syncStatus = label("", 15, GREEN, false); syncStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); mediaDetails.addView(syncStatus);
        detail = label("", 14, MUTED, false); spacing(detail, 4, 0); mediaDetails.addView(detail);
        notice = label("", 14, AMBER, false); spacing(notice, 4, 0); mediaDetails.addView(notice);
        metadata.addView(mediaDetails, new LinearLayout.LayoutParams(-1, -2));
        if (!split && model.inRoom) {
            HorizontalScrollView members = new HorizontalScrollView(context); members.setHorizontalScrollBarEnabled(false);
            LinearLayout chips = row(); chips.setPadding(dp(16), 0, dp(16), dp(8)); memberRows = chips; members.addView(chips);
            metadata.addView(members);
            memberStrip = members;
        }
        watching.addView(metadataScroll, new LinearLayout.LayoutParams(-1, -2));
    }

    private void buildChatPanel() {
        chatPanel = column(); chatPanel.setBackgroundColor(SURFACE); chatPanel.setPadding(dp(16), 0, dp(16), dp(8));
        if (Build.VERSION.SDK_INT >= 28) chatPanel.setAccessibilityPaneTitle(context.getString(R.string.chat_pane_title));
        else chatPanel.setContentDescription(context.getString(R.string.chat_pane_title));
        LinearLayout tabs = row(); chatTabs = tabs;
        Button chats = textButton("聊天", () -> selectTab(false)); Button members = textButton("成员", () -> selectTab(true));
        tabs.addView(chats, weight()); tabs.addView(members, weight());
        if (fullscreen) tabs.addView(icon(R.drawable.ic_close, "关闭聊天", this::closeDrawer));
        chatPanel.addView(tabs);
        chatScroll = new ScrollView(context); chatScroll.setFillViewport(true); chatPanel.addView(chatScroll, new LinearLayout.LayoutParams(-1, 0, 1));
        chatLines = column(); chatLines.setPadding(0, dp(4), 0, dp(12)); chatScroll.addView(chatLines);
        if (history.isEmpty()) {
            TextView empty = label(model.inRoom ? "聊聊这部影片吧" : "进入房间后和朋友聊天", 15, MUTED, false); empty.setTag("empty"); chatLines.addView(empty);
        } else for (ChatLine line : history) addChatLine(line);
        LinearLayout compose = row(); compose.setGravity(Gravity.BOTTOM);
        composer = input("聊两句…", "聊天内容，最多 500 字"); composer.setSingleLine(false); composer.setMaxLines(3);
        composer.setFilters(new InputFilter[]{new InputFilter.LengthFilter(500)}); composer.setText(draft);
        composer.setImeOptions(android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI);
        compose.addView(composer, weight());
        send = button("发送", true, this::sendDraft); send.setSingleLine(true);
        int sendWidth = Math.max(dp(72), (int) Math.ceil(send.getPaint().measureText(send.getText().toString()))
                + send.getCompoundPaddingLeft() + send.getCompoundPaddingRight());
        LinearLayout.LayoutParams sendParams = new LinearLayout.LayoutParams(sendWidth, -2); sendParams.setMarginStart(dp(8)); compose.addView(send, sendParams);
        chatPanel.addView(compose); composer.setEnabled(model.inRoom); send.setEnabled(model.inRoom && model.connected);
        if (membersSelected) selectTab(true);
    }

    private void bind() {
        if (!builtWatching) {
            create.setEnabled(!model.busy); open.setEnabled(!model.busy);
            restoreSection.setVisibility(model.canRestore ? View.VISIBLE : View.GONE); restore.setEnabled(!model.busy);
            homeStatus.setText(nonempty(model.notice, nonempty(model.status, "视频在本机播放，房间同步控制与聊天")));
            return;
        }
        if (!fullscreen) {
            headerTitle.setText(model.inRoom ? model.roomTitle : "本机观看");
            headerSubtitle.setText(model.inRoom ? (model.connected ? model.members.size() + " / 5 人" : "正在重连") : "创建房间，邀请朋友一起看");
            mediaTitle.setText(model.mediaTitle); change.setEnabled(controlsAllowed() && !model.busy);
            syncStatus.setText(nonempty(model.status, stateLabel())); syncStatus.setTextColor(stateColor());
            detail.setText(model.detail); detail.setVisibility(TextUtils.isEmpty(model.detail) ? View.GONE : View.VISIBLE);
            notice.setText(model.notice); notice.setVisibility(TextUtils.isEmpty(model.notice) ? View.GONE : View.VISIBLE);
        } else fullscreenTitle.setText(model.mediaTitle);
        play.setImageResource(model.playing ? R.drawable.ic_pause : R.drawable.ic_play);
        play.setContentDescription(model.playing ? "暂停" : "播放");
        play.setEnabled(controlsAllowed() && !model.busy && model.playbackState != PlaybackState.EMPTY);
        play.setAlpha(play.isEnabled() ? 1 : .4f);
        progress.setEnabled(controlsAllowed() && duration > 0 && !model.busy);
        rateButton.setText(String.format(Locale.ROOT, "%s×", model.rate)); rateButton.setEnabled(controlsAllowed()); rateButton.setAlpha(controlsAllowed() ? 1 : .4f);
        String overlay = overlayLabel(); playerState.setText(overlay);
        playbackOverlay.setVisibility(overlay.isEmpty() ? View.GONE : View.VISIBLE);
        boolean actionable = model.playbackState == PlaybackState.RETRYABLE_ERROR || model.playbackState == PlaybackState.NEEDS_LOGIN
                || model.playbackState == PlaybackState.NEEDS_GESTURE || model.playbackState == PlaybackState.UNSUPPORTED;
        overlayAction.setVisibility(actionable ? View.VISIBLE : View.GONE);
        overlayAction.setText(model.playbackState == PlaybackState.NEEDS_LOGIN ? "登录 B 站" : model.playbackState == PlaybackState.NEEDS_GESTURE ? "启用本机播放" : model.playbackState == PlaybackState.UNSUPPORTED ? "重新选片" : "重试播放");
        overlayAction.setEnabled(!model.busy && (model.playbackState != PlaybackState.UNSUPPORTED || controlsAllowed()));
        composer.setEnabled(model.inRoom); send.setEnabled(model.inRoom && model.connected);
        updateMembers(); updatePosition(position, duration);
        if (!model.playing || !lastRenderedPlaying) showControls(true);
        lastRenderedPlaying = model.playing;
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
                TextView name = label(member.nickname + (member.self ? "（你）" : ""), 14, TEXT, true);
                name.setMaxWidth(dp(144)); name.setMaxLines(2); item.addView(name);
                item.addView(label((member.host ? "房主 · " : "") + member.status, 13, MUTED, false));
                memberRows.addView(item);
            }
            if (model.inRoom) memberRows.addView(textButton("邀请", actions::invite));
        }
        if (membersSelected) selectTab(true);
    }

    private void selectTab(boolean members) {
        membersSelected = members; chatLines.removeAllViews();
        if (members) {
            for (Member member : model.members) {
                TextView item = label(member.nickname + (member.self ? "（你）" : "") + "\n" + (member.host ? "房主 · " : "") + member.status, 16, TEXT, false);
                item.setPadding(0, dp(12), 0, dp(12)); chatLines.addView(item);
            }
            if (!model.inRoom) chatLines.addView(label("尚未加入房间", 16, MUTED, false));
        } else {
            for (ChatLine line : history) addChatLine(line);
            clearPreviews(); scrollChat();
        }
    }

    private void addChatLine(ChatLine line) {
        if (membersSelected) return;
        View empty = chatLines.findViewWithTag("empty"); if (empty != null) chatLines.removeView(empty);
        LinearLayout message = column(); message.setPadding(0, dp(8), 0, dp(8)); message.setGravity(line.own ? Gravity.END : Gravity.START);
        message.addView(label(line.nickname + (line.own ? "（你）" : ""), 12, MUTED, false));
        TextView body = label(line.text, 16, line.own ? BG : TEXT, false); body.setTextIsSelectable(true);
        body.setPadding(dp(12), dp(8), dp(12), dp(8)); body.setBackground(shape(line.own ? CYAN : BORDER, 12));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, -2); params.topMargin = dp(4); message.addView(body, params);
        chatLines.addView(message);
    }

    private void sendDraft() {
        String value = composer.getText().toString();
        if (value.trim().isEmpty()) { composer.setError("写点内容再发送"); return; }
        pendingChat = value; draft = value; actions.sendChat(value);
    }

    private void openDrawer() {
        if (!fullscreen || !model.inRoom || drawerLayer == null) return;
        drawerOpen = true; clearPreviews(); drawerLayer.setVisibility(View.VISIBLE);
        chatPanel.setVisibility(View.VISIBLE); screen.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS);
        // The editor stays unfocused until the user explicitly taps it.
        chatPanel.setFocusableInTouchMode(true); chatPanel.requestFocus();
        main.removeCallbacks(hideControls); scrollChat();
    }

    private void closeDrawer() {
        draft = composer == null ? draft : composer.getText().toString();
        hideKeyboard(); drawerOpen = false;
        if (drawerLayer != null) drawerLayer.setVisibility(View.GONE);
        screen.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_AUTO); showControls(true);
    }

    private boolean videoTouch(View view, MotionEvent event) {
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN: touchX = event.getX(); touchY = event.getY(); movement = 0; return true;
            case MotionEvent.ACTION_MOVE: movement = Math.max(movement, (float) Math.hypot(event.getX() - touchX, event.getY() - touchY)); return true;
            case MotionEvent.ACTION_UP:
                if (fullscreen && model.inRoom && CinemaInteraction.opensChat(touchX, touchY, video.getWidth(), video.getHeight(), dp(24),
                        topControls != null && topControls.getVisibility() == View.VISIBLE ? topControls.getHeight() : dp(24),
                        bottomControls.getVisibility() == View.VISIBLE ? bottomControls.getHeight() : dp(32), movement,
                        ViewConfiguration.get(context).getScaledTouchSlop())) openDrawer();
                else if (movement <= ViewConfiguration.get(context).getScaledTouchSlop()) view.performClick();
                return true;
            case MotionEvent.ACTION_CANCEL: return true;
            default: return true;
        }
    }

    private void showControls(boolean value) {
        if (bottomControls == null) return;
        AccessibilityManager accessibility = (AccessibilityManager) context.getSystemService(Context.ACCESSIBILITY_SERVICE);
        controlsVisible = value || !model.playing || (accessibility != null && accessibility.isTouchExplorationEnabled());
        bottomControls.setVisibility(controlsVisible ? View.VISIBLE : View.GONE);
        if (topControls != null) topControls.setVisibility(controlsVisible ? View.VISIBLE : View.GONE);
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
        message.setMinLines(1); message.setMaxLines(3); message.setEllipsize(TextUtils.TruncateAt.END);
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
        int timeout = 4500;
        if (Build.VERSION.SDK_INT >= 29 && accessibility != null) timeout = accessibility.getRecommendedTimeoutMillis(timeout, AccessibilityManager.FLAG_CONTENT_TEXT);
        main.postDelayed(nextPreview, timeout);
    }

    private void clearPreviews() {
        main.removeCallbacks(nextPreview); previews.clear();
        if (preview != null) { root.removeView(preview); preview = null; }
    }

    private void applyInsets() {
        root.setPadding(safeLeft, safeTop, safeRight, Math.max(safeBottom, imeBottom));
        if (composer != null) composer.setMaxLines(keyboardVisible ? 1 : 3);
        if (chatTabs != null) {
            chatTabs.getChildAt(0).setVisibility(keyboardVisible ? View.GONE : View.VISIBLE);
            chatTabs.getChildAt(1).setVisibility(keyboardVisible ? View.GONE : View.VISIBLE);
            // In the drawer the close button remains available while typing.
            chatTabs.setVisibility(keyboardVisible && !fullscreen ? View.GONE : View.VISIBLE);
        }
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
        AlertDialog dialog = new AlertDialog.Builder(context).setTitle("加入房间").setView(dialogContent(value))
                .setNegativeButton("取消", null).setPositiveButton("加入", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            String text = value.getText().toString().trim();
            if (text.isEmpty()) { value.setError("请粘贴邀请链接或房间码"); return; }
            actions.joinRoom(text); dialog.dismiss();
        })); dialog.show();
    }

    private void mediaDialog() {
        EditText value = input("粘贴 B 站链接或 BV 号", "影片链接"); value.setText(mediaDraft);
        AlertDialog dialog = new AlertDialog.Builder(context).setTitle("选择影片").setView(dialogContent(value))
                .setNegativeButton("取消", null).setPositiveButton("打开影片", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            String text = value.getText().toString().trim();
            if (text.isEmpty()) { value.setError("请输入影片链接或编号"); return; }
            mediaDraft = text; actions.selectMedia(text); dialog.dismiss();
        })); dialog.show();
    }

    private void nicknameDialog() {
        EditText value = input("你的房间昵称", "房间昵称"); value.setText(model.nickname);
        value.setFilters(new InputFilter[]{new InputFilter.LengthFilter(24)});
        AlertDialog dialog = new AlertDialog.Builder(context).setTitle("修改房间昵称").setMessage("昵称与 B 站账号无关")
                .setView(dialogContent(value)).setNegativeButton("取消", null).setPositiveButton("保存", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            String text = value.getText().toString().trim();
            if (text.isEmpty()) { value.setError("请输入 1–24 字昵称"); return; }
            actions.setNickname(text); dialog.dismiss();
        })); dialog.show();
    }

    private void accountMenu(View anchor) {
        PopupMenu menu = new PopupMenu(context, anchor);
        menu.getMenu().add(model.loggedIn ? "检查 B 站登录" : model.accountLabel).setOnMenuItemClickListener(item -> { actions.login(); return true; });
        menu.getMenu().add("修改房间昵称").setOnMenuItemClickListener(item -> { nicknameDialog(); return true; });
        menu.getMenu().add("检查更新").setOnMenuItemClickListener(item -> { actions.checkUpdate(); return true; }); menu.show();
    }

    private void roomMenu(View anchor) {
        PopupMenu menu = new PopupMenu(context, anchor);
        if (controlsAllowed()) menu.getMenu().add("选择影片").setOnMenuItemClickListener(item -> { mediaDialog(); return true; });
        if (!model.inRoom) {
            menu.getMenu().add("创建房间").setOnMenuItemClickListener(item -> { actions.createRoom(""); return true; });
            menu.getMenu().add("加入房间").setOnMenuItemClickListener(item -> { joinDialog(); return true; });
        }
        menu.getMenu().add("检查 B 站登录").setOnMenuItemClickListener(item -> { actions.login(); return true; });
        menu.getMenu().add("修改房间昵称").setOnMenuItemClickListener(item -> { nicknameDialog(); return true; });
        if (model.inRoom) menu.getMenu().add("退出房间").setOnMenuItemClickListener(item -> { leaveDialog(); return true; });
        menu.getMenu().add("检查更新").setOnMenuItemClickListener(item -> { actions.checkUpdate(); return true; }); menu.show();
    }

    private void chooseRate() {
        if (!controlsAllowed()) return;
        double[] values = {.5, .75, 1, 1.25, 1.5, 2};
        String[] labels = {"0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×"};
        new AlertDialog.Builder(context).setTitle("播放倍速").setItems(labels, (dialog, index) -> actions.rate(values[index])).setNegativeButton("取消", null).show();
    }

    private void leaveDialog() {
        if (!model.inRoom) { actions.leaveRoom(); return; }
        new AlertDialog.Builder(context).setTitle("退出放映室？").setMessage("之后可通过有效邀请再次加入")
                .setNegativeButton("继续观看", null).setPositiveButton("退出房间", (dialog, which) -> actions.leaveRoom()).show();
    }

    private void playbackAction() {
        if (model.playbackState == PlaybackState.NEEDS_LOGIN) actions.login();
        else if (model.playbackState == PlaybackState.UNSUPPORTED) mediaDialog();
        else actions.retryPlayback();
    }

    private void pasteMedia() {
        ClipboardManager clipboard = (ClipboardManager) context.getSystemService(Context.CLIPBOARD_SERVICE);
        if (clipboard != null && clipboard.hasPrimaryClip() && clipboard.getPrimaryClip() != null && clipboard.getPrimaryClip().getItemCount() > 0) {
            CharSequence text = clipboard.getPrimaryClip().getItemAt(0).coerceToText(context);
            if (text != null) mediaInput.setText(text);
        }
    }

    private String overlayLabel() {
        switch (model.playbackState) {
            case EMPTY: return "选择影片，开始一起看";
            case LOADING: return "正在准备本机影片…";
            case BUFFERING: return "本机正在缓冲…";
            case CATCHING_UP: return "正在追上房主进度…";
            case NEEDS_LOGIN: return "此影片需要本机登录 B 站";
            case NEEDS_GESTURE: return "点击启用本机播放";
            case RETRYABLE_ERROR: return "影片暂时加载失败";
            case UNSUPPORTED: return "本机暂不支持这部影片";
            case RECONNECTING: return "正在重新连接房间…";
            default: return "";
        }
    }

    private String stateLabel() {
        if (!model.connected && model.inRoom) return "房间连接已断开 · 正在重连";
        if (model.playbackState == PlaybackState.PLAYING) return model.inRoom ? (model.isHost ? "房主正在播放" : "跟随房主播放") : "正在播放";
        if (model.playbackState == PlaybackState.READY || model.playbackState == PlaybackState.PAUSED) return model.inRoom ? "本机已就绪 · 跟随房主时间轴" : "本机已就绪";
        return nonempty(overlayLabel(), "房间已连接");
    }

    private int stateColor() {
        return model.playbackState == PlaybackState.READY || model.playbackState == PlaybackState.PLAYING || model.playbackState == PlaybackState.PAUSED ? GREEN : AMBER;
    }

    private boolean controlsAllowed() { return !model.inRoom || model.canControl; }
    private void scrollChat() { if (chatScroll != null && !membersSelected) chatScroll.post(() -> chatScroll.fullScroll(View.FOCUS_DOWN)); }
    private View dialogContent(EditText input) { LinearLayout wrap = column(); wrap.setPadding(dp(24), dp(8), dp(24), dp(8)); wrap.addView(input); return wrap; }
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
        value.setPadding(dp(12), dp(8), dp(12), dp(8)); value.setBackground(touchBackground(BG, 12));
        value.setSelectAllOnFocus(false); value.setImeOptions(android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI); return value;
    }
    private Button button(String text, boolean primary, Runnable action) {
        Button button = new Button(context); button.setText(text); button.setAllCaps(false); button.setTextSize(16);
        button.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled}, new int[]{}},
                new int[]{primary ? BORDER : MUTED, primary ? BG : TEXT})); button.setMinHeight(dp(52)); button.setMinimumHeight(dp(52));
        button.setPadding(dp(16), dp(8), dp(16), dp(8)); button.setBackground(touchBackground(primary ? CYAN : SURFACE, 12));
        button.setOnClickListener(view -> action.run()); button.setStateListAnimator(null); return button;
    }
    private Button textButton(String text, Runnable action) {
        Button value = button(text, false, action); value.setTextColor(CYAN); value.setBackground(touchBackground(Color.TRANSPARENT, 12)); return value;
    }
    private ImageButton icon(int drawable, String description, Runnable action) {
        ImageButton value = new ImageButton(context); value.setImageResource(drawable); value.setImageTintList(ColorStateList.valueOf(TEXT));
        value.setContentDescription(description); value.setBackground(touchBackground(Color.TRANSPARENT, 24)); value.setPadding(dp(12), dp(12), dp(12), dp(12));
        value.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48))); value.setOnClickListener(view -> action.run()); return value;
    }
    private GradientDrawable shape(int color, int radius) { GradientDrawable value = new GradientDrawable(); value.setColor(color); value.setCornerRadius(dp(radius)); value.setStroke(dp(1), BORDER); return value; }
    private RippleDrawable touchBackground(int color, int radius) { return new RippleDrawable(ColorStateList.valueOf(0x4464C9DF), shape(color, radius), shape(Color.WHITE, radius)); }
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
