using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DownKyi.Services.Watch;

internal sealed record WatchRoomActionNotice(int Id, string Text, int RemainingMilliseconds);

// The player is a native WebView, so desktop controls cannot reliably draw above it.
// Keep the chat layer inside the existing player and insert user text only as text nodes.
internal static class RoomChatOverlayScript
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Build(
        IReadOnlyList<WatchRoomChatMessage> messages,
        WatchRoomChatMessage? toast,
        string? ownMemberId,
        bool fullscreen,
        bool inRoom,
        int memberCount,
        int revision,
        int toastSequence,
        int toastRemainingMilliseconds,
        IReadOnlyList<WatchRoomActionNotice> hostActionNotices,
        int hostActionToastSequence,
        bool dockedChat = false)
    {
        var state = new
        {
            fullscreen,
            dockedChat,
            inRoom,
            memberCount,
            revision,
            toastSequence,
            toastRemainingMilliseconds,
            hostActionToastSequence,
            hostActionNotices = hostActionNotices.Where(notice => notice.RemainingMilliseconds > 0)
                .Select(notice => new { notice.Id, notice.Text, notice.RemainingMilliseconds }).ToArray(),
            messages = messages.Select(message => new
            {
                message.MemberId,
                message.Role,
                message.Nickname,
                message.Text,
                message.SentAtUnixMs,
                isSelf = string.Equals(message.MemberId, ownMemberId, StringComparison.Ordinal)
            }).ToArray(),
            toast = toast == null || toastRemainingMilliseconds == 0
                ? null : new { toast.Nickname, toast.Text }
        };

        return "(() => { const state = " + JsonSerializer.Serialize(state, JsonOptions) + ";\n" + Script;
    }

    private const string Script = """
        const player = window.__biliCinemaPlayerRoot;
        if (!player?.isConnected) return false;
        const make = (tag, name) => {
            const element = document.createElement(tag); element.className = name; return element;
        };
        let ui = window.__biliCinemaChatUi;
        if (!ui || !ui.host.isConnected) {
            ui?.listenerAbort?.abort();
            ui?.edgeObserver?.disconnect();
            clearTimeout(ui?.closeTimer);
            const host = document.createElement('div');
            host.id = 'bc-room-chat';
            const css = document.createElement('style');
            css.textContent = `
                #bc-room-chat { position:fixed!important; inset:0!important; z-index:2147483647!important;
                    color:#f8f9fa; font:14px/1.45 'Segoe UI','Microsoft YaHei',sans-serif;
                    pointer-events:none!important; }
                #bc-room-chat * { box-sizing:border-box; }
                #bc-room-chat .bc-edge { position:absolute; top:0; right:0; bottom:96px; width:18px;
                    pointer-events:auto; cursor:pointer; }
                #bc-room-chat .bc-fullscreen-open { display:none; position:absolute; top:16px; right:20px;
                    width:40px; height:40px; align-items:center; justify-content:center; padding:9px;
                    border:1px solid rgba(255,255,255,.2); border-radius:10px; color:#f7f9fc;
                    background:rgba(10,16,24,.72); backdrop-filter:blur(10px); cursor:pointer;
                    pointer-events:auto!important; }
                #bc-room-chat .bc-fullscreen-open:hover { background:rgba(28,40,55,.92); }
                #bc-room-chat .bc-fullscreen-open svg { display:block; width:22px; height:22px; }
                #bc-room-chat.bc-fullscreen:not(.bc-open):not(.bc-docked) .bc-fullscreen-open { display:flex; }
                #bc-room-chat.bc-docked .bc-edge,#bc-room-chat.bc-docked .bc-panel { display:none!important; }
                #bc-room-chat .bc-panel { position:absolute; top:0; right:0; bottom:0;
                    width:min(320px, 38vw); min-width:260px; display:flex; flex-direction:column;
                    background:rgba(8,12,18,.88); backdrop-filter:blur(14px) saturate(.75);
                    border-left:1px solid rgba(255,255,255,.16); box-shadow:-14px 0 34px rgba(0,0,0,.14);
                    transform:translateX(102%); opacity:0; pointer-events:none;
                    transition:transform .18s ease,opacity .18s ease; }
                #bc-room-chat.bc-open .bc-panel { transform:none; opacity:1; pointer-events:auto; }
                #bc-room-chat .bc-head { display:flex; align-items:center; justify-content:space-between;
                    min-height:62px; padding:17px 18px; border-bottom:1px solid rgba(255,255,255,.15); }
                #bc-room-chat .bc-title { font-size:16px; font-weight:650; }
                #bc-room-chat .bc-count { font-size:12px; color:#e4e8e9; }
                #bc-room-chat .bc-count::before { content:''; display:inline-block; width:7px; height:7px;
                    margin-right:6px; border-radius:50%; background:#a6c7b9; }
                #bc-room-chat .bc-list { flex:1; min-height:0; overflow-y:auto; padding:15px 16px;
                    scrollbar-color:rgba(255,255,255,.3) transparent; }
                #bc-room-chat .bc-empty { color:#d4d9da; font-size:13px; padding:12px 0; }
                #bc-room-chat .bc-message { display:flex; gap:9px; margin-bottom:16px; }
                #bc-room-chat .bc-message.bc-self { flex-direction:row-reverse; }
                #bc-room-chat .bc-avatar { flex:0 0 27px; width:27px; height:27px;
                    display:flex; align-items:center; justify-content:center; border-radius:50%;
                    background:#8999a1; color:#fff; font-size:12px; font-weight:700; }
                #bc-room-chat .bc-message.bc-guest .bc-avatar { background:#99918e; }
                #bc-room-chat .bc-message-main { flex:1; min-width:0; }
                #bc-room-chat .bc-self .bc-message-main { text-align:right; }
                #bc-room-chat .bc-meta { display:flex; justify-content:space-between; gap:7px; margin-bottom:3px; }
                #bc-room-chat .bc-self .bc-meta { flex-direction:row-reverse; }
                #bc-room-chat .bc-name { color:#e3ebee; font-size:12px; font-weight:650;
                    overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
                #bc-room-chat .bc-guest .bc-name { color:#e5e1de; }
                #bc-room-chat .bc-time { flex:0 0 auto; color:#c8cfd1; font-size:11px; }
                #bc-room-chat .bc-body { color:#fff; font-size:13px; white-space:pre-wrap;
                    overflow-wrap:anywhere; display:inline-block; max-width:100%; text-align:left;
                    padding:8px 10px; border-radius:8px; background:#202020; }
                #bc-room-chat .bc-self .bc-body { background:#20374b; }
                #bc-room-chat .bc-compose { padding:12px 14px 16px;
                    border-top:1px solid rgba(255,255,255,.14); }
                #bc-room-chat .bc-input { flex:1; min-width:0; width:100%; height:40px; min-height:40px;
                    max-height:40px; resize:none;
                    border-radius:9px; border:1px solid rgba(255,255,255,.33); outline:none;
                    padding:10px 11px; color:#fff; background:rgba(255,255,255,.125);
                    font:13px/1.45 'Segoe UI','Microsoft YaHei',sans-serif; }
                #bc-room-chat .bc-input:focus { border-color:#b5c7d0; box-shadow:0 0 0 2px rgba(181,199,208,.25); }
                #bc-room-chat .bc-input::placeholder { color:#d4d9da; }
                #bc-room-chat .bc-actions { display:flex; align-items:center; gap:7px; }
                #bc-room-chat .bc-hint { font-size:11px; color:#d4d9da; white-space:pre-line; }
                #bc-room-chat .bc-send { flex:0 0 40px; width:40px; min-width:40px; height:40px; padding:8px;
                    border:0; border-radius:8px; color:#fff;
                    background:#2589f5; font-weight:600;
                    cursor:pointer; }
                #bc-room-chat .bc-send:hover { background:#4b9fff; }
                #bc-room-chat .bc-send svg { display:block; width:20px; height:20px; }
                #bc-room-chat .bc-close { border:0; border-radius:6px; width:32px; height:32px;
                    color:#d9e1ea; background:transparent; font-size:22px; line-height:1; cursor:pointer; }
                #bc-room-chat .bc-close:hover { background:rgba(255,255,255,.12); }
                #bc-room-chat .bc-send:focus-visible { outline:2px solid #fff; outline-offset:2px; }
                #bc-room-chat .bc-toast { position:absolute; right:20px; top:38%; width:min(310px,40vw);
                    padding:12px 15px; border-radius:11px; color:#fff;
                    background:rgba(52,54,56,.78); backdrop-filter:blur(9px) saturate(.75);
                    box-shadow:0 8px 24px rgba(0,0,0,.25); opacity:0; visibility:hidden;
                    transition:opacity .16s ease; pointer-events:none; }
                #bc-room-chat .bc-toast.bc-visible { opacity:1; visibility:visible; }
                #bc-room-chat.bc-open .bc-toast { right:min(340px, 41vw); }
                #bc-room-chat .bc-toast-name { color:#e3ebee; font-weight:650; font-size:12px; margin-bottom:3px; }
                #bc-room-chat .bc-toast-body { font-size:13px; white-space:pre-wrap; overflow-wrap:anywhere; }
                #bc-room-chat .bc-action-toasts { position:absolute; left:20px; top:38%;
                    max-width:min(280px,38vw); display:flex; flex-direction:column; gap:7px;
                    pointer-events:none; }
                #bc-room-chat .bc-action-toast { padding:10px 14px; border-radius:9px;
                    color:#f5f7f8; background:rgba(35,46,52,.78);
                    border-left:3px solid #8bb5c7; backdrop-filter:blur(7px);
                    box-shadow:0 8px 24px rgba(0,0,0,.2); font-size:13px; }
                @media (prefers-reduced-motion:reduce) {
                    #bc-room-chat .bc-panel,#bc-room-chat .bc-toast,
                    #bc-room-chat .bc-action-toast { transition:none; }
                }
            `;
            const edge = document.createElement('div'); edge.className = 'bc-edge';
            edge.setAttribute('role', 'button'); edge.tabIndex = 0;
            edge.setAttribute('aria-label', '打开房间聊天');
            edge.setAttribute('aria-controls', 'bc-chat-panel');
            edge.setAttribute('aria-expanded', 'false');
            const fullscreenOpen = make('button', 'bc-fullscreen-open');
            fullscreenOpen.type = 'button';
            fullscreenOpen.setAttribute('aria-label', '打开聊天');
            fullscreenOpen.title = '打开聊天';
            fullscreenOpen.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 11.5C21 16.2 17 20 12 20c-1.6 0-3.2-.4-4.5-1L3 21l1.2-4.5C2.9 15.1 2 13.4 2 11.5 2 6.8 6.5 3 12 3s9 3.8 9 8.5ZM7 11h.01M12 11h.01M17 11h.01" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
            const panel = document.createElement('section'); panel.className = 'bc-panel';
            panel.id = 'bc-chat-panel';
            panel.setAttribute('aria-label', '房间聊天');
            const head = make('div', 'bc-head');
            const title = make('span', 'bc-title'); title.textContent = '房间聊天';
            const count = make('span', 'bc-count');
            const closeButton = make('button', 'bc-close'); closeButton.type = 'button';
            closeButton.textContent = '×'; closeButton.setAttribute('aria-label', '关闭聊天');
            head.append(title, count, closeButton);
            const list = make('div', 'bc-list');
            list.setAttribute('role', 'log'); list.setAttribute('aria-live', 'polite');
            const compose = make('div', 'bc-compose');
            const input = make('input', 'bc-input'); input.type = 'text';
            input.maxLength = 500; input.placeholder = '说点什么…';
            input.setAttribute('aria-label', '聊天消息');
            const actions = make('div', 'bc-actions');
            const hint = make('span', 'bc-hint'); hint.textContent = 'Enter 发送 · Alt 开关聊天 · Esc 收起';
            const sendButton = make('button', 'bc-send');
            sendButton.type = 'button'; sendButton.setAttribute('aria-label', '发送');
            sendButton.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 11.5 21 3l-6.5 18-3-7.5L3 11.5Zm8.5 2L21 3" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
            actions.append(input, sendButton); compose.append(actions, hint);
            panel.append(head, list, compose);
            const toast = document.createElement('div'); toast.className = 'bc-toast';
            toast.setAttribute('role', 'status');
            const toastName = make('div', 'bc-toast-name');
            const toastBody = make('div', 'bc-toast-body'); toast.append(toastName, toastBody);
            const actionToasts = make('div', 'bc-action-toasts');
            actionToasts.setAttribute('role', 'status');
            host.append(css, edge, fullscreenOpen, panel, toast, actionToasts);
            ui = { host, edge, panel, list, input, count, toast, toastName, toastBody,
                actionToasts, closeButton, fullscreenOpen, revision:-1, toastSequence:-1, actionSequence:-1,
                fullscreen:false, inRoom:false, toastTimer:null, actionTimers:[],
                closeTimer:null, editing:false, composing:false, altChatPending:false,
                escapeDismissed:false, dismissClickPending:false, edgeObserver:null,
                listenerAbort:new AbortController() };
            window.__biliCinemaChatUi = ui;
            const send = () => {
                const text = ui.input.value.trim();
                if (!text || text.length > 500 || !ui.inRoom) return;
                if (typeof invokeCSharpAction === 'function') {
                    invokeCSharpAction(JSON.stringify({source:'biliCinemaChat',type:'send',text}));
                    ui.input.value = '';
                    ui.input.focus({preventScroll:true});
                }
            };
            sendButton.addEventListener('click', send);
            ui.input.addEventListener('keydown', event => {
                if (event.key === 'Enter' && !event.shiftKey && !event.isComposing
                    && !ui.composing && event.keyCode !== 229) {
                    event.preventDefault(); send();
                }
            });
            for (const type of ['click','dblclick','pointerdown','pointerup','pointermove',
                'mousedown','mouseup','mousemove','keydown','keyup','wheel']) {
                host.addEventListener(type, event => event.stopPropagation());
            }
            const setOpen = (open, focusInput) => {
                clearTimeout(ui.closeTimer);
                ui.closeTimer = null;
                const wasOpen = ui.host.classList.contains('bc-open');
                ui.host.classList.toggle('bc-open', open);
                ui.edge.setAttribute('aria-expanded', open ? 'true' : 'false');
                if (!open) {
                    ui.editing = false;
                    ui.composing = false;
                    ui.input.blur();
                } else if (!wasOpen && focusInput) ui.input.focus({preventScroll:true});
                if (wasOpen !== open && typeof invokeCSharpAction === 'function') {
                    invokeCSharpAction(JSON.stringify({source:'biliCinemaChat',
                        type:'panelState',open}));
                }
            };
            const close = () => setOpen(false, false);
            ui.closePanel = close;
            ui.openPanel = () => setOpen(true, true);
            ui.togglePanel = () => {
                if (!ui.inRoom) return;
                if (ui.dockedChat) {
                    if (typeof invokeCSharpAction === 'function')
                        invokeCSharpAction(JSON.stringify({source:'biliCinemaChat',type:'toggleDockedPanel'}));
                } else setOpen(!ui.host.classList.contains('bc-open'), true);
            };
            edge.addEventListener('click', () => setOpen(true, true));
            fullscreenOpen.addEventListener('click', () => setOpen(true, true));
            closeButton.addEventListener('click', close);
            // Reserve the control bar even when Bilibili has hidden its buttons.
            // The edge's hit area and the hover decision use the same boundary.
            ui.updateEdgeArea = () => {
                const root = window.__biliCinemaPlayerRoot;
                const controls = root?.querySelector('.bpx-player-control-wrap, .bilibili-player-video-control');
                const rect = controls?.getBoundingClientRect();
                const frame = root?.querySelector('.bpx-player-primary-area, .bilibili-player-area')
                    || root?.querySelector('.bpx-player-video-area, video') || root;
                const frameBottom = frame?.getBoundingClientRect().bottom ?? innerHeight;
                const controlHeight = rect?.height > 0 ? rect.height : 64;
                const controlTop = Math.min(rect?.height > 0 ? rect.top : innerHeight,
                    frameBottom - controlHeight);
                const reserved = Math.max(96, innerHeight - controlTop + 12);
                const bottom = Math.min(innerHeight, reserved);
                ui.edge.style.bottom = `${bottom}px`;
                return innerHeight - bottom;
            };
            window.addEventListener('resize', ui.updateEdgeArea, {signal:ui.listenerAbort.signal});
            ui.edgeObserver = new ResizeObserver(() => ui.updateEdgeArea());
            const root = window.__biliCinemaPlayerRoot;
            const controls = root?.querySelector('.bpx-player-control-wrap, .bilibili-player-video-control');
            const frame = root?.querySelector('.bpx-player-primary-area, .bilibili-player-area')
                || root?.querySelector('.bpx-player-video-area, video') || root;
            if (controls) ui.edgeObserver.observe(controls);
            if (frame && frame !== controls) ui.edgeObserver.observe(frame);
            // Keep chat open while typing so IME and pointer movement never steal focus.
            const keepOpen = () => ui.editing || ui.composing || ui.input.value.length > 0;
            const deferClose = () => {
                if (keepOpen() || ui.closeTimer || !ui.host.classList.contains('bc-open')) return;
                ui.closeTimer = setTimeout(() => {
                    ui.closeTimer = null;
                    if (!keepOpen()) close();
                }, 500);
            };
            document.addEventListener('pointermove', event => {
                if (!event.isTrusted || !ui.inRoom || !ui.fullscreen || ui.dockedChat
                    || event.pointerType === 'touch') return;
                const overPanel = ui.host.classList.contains('bc-open')
                    && ui.panel.contains(event.target);
                const overEdge = event.clientX >= innerWidth - 18
                    && event.clientY >= 0 && event.clientY < ui.updateEdgeArea();
                if (overEdge || overPanel) setOpen(true, false);
                else deferClose();
            }, {capture:true, signal:ui.listenerAbort.signal});
            document.addEventListener('mouseleave', deferClose, {signal:ui.listenerAbort.signal});
            const beginEditing = () => {
                ui.editing = true;
                clearTimeout(ui.closeTimer);
                ui.closeTimer = null;
            };
            ui.input.addEventListener('pointerdown', beginEditing);
            ui.input.addEventListener('input', beginEditing);
            ui.input.addEventListener('compositionstart', () => {
                ui.composing = true;
                beginEditing();
            });
            ui.input.addEventListener('compositionend', () => { ui.composing = false; });
            edge.addEventListener('keydown', event => {
                if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    setOpen(true, true);
                }
            });
            ui.input.addEventListener('keydown', event => {
                if (event.isComposing || ui.composing || event.keyCode === 229) return;
                beginEditing();
            });
            // Capture both halves and toggle on release: held Alt must not
            // repeat, and Alt+another key must retain its normal meaning.
            window.addEventListener('keydown', event => {
                if (event.key === 'Alt' && !event.ctrlKey && !event.shiftKey && !event.metaKey
                    && ui.inRoom) {
                    event.preventDefault();
                    event.stopImmediatePropagation();
                    if (!event.repeat) ui.altChatPending = true;
                } else ui.altChatPending = false;
            }, {capture:true, signal:ui.listenerAbort.signal});
            window.addEventListener('keyup', event => {
                if (event.key === 'Alt' && ui.altChatPending) {
                    event.preventDefault();
                    event.stopImmediatePropagation();
                    ui.altChatPending = false;
                    ui.togglePanel();
                }
            }, {capture:true, signal:ui.listenerAbort.signal});
            window.addEventListener('blur', () => { ui.altChatPending = false; },
                {signal:ui.listenerAbort.signal});
            document.addEventListener('pointerdown', event => {
                if (ui.host.classList.contains('bc-open') && !ui.panel.contains(event.target)
                    && !ui.edge.contains(event.target)) {
                    ui.dismissClickPending = true;
                    event.preventDefault();
                    event.stopImmediatePropagation();
                    close();
                }
            }, {capture:true, signal:ui.listenerAbort.signal});
            document.addEventListener('click', event => {
                if (!ui.dismissClickPending) return;
                ui.dismissClickPending = false;
                event.preventDefault();
                event.stopImmediatePropagation();
            }, {capture:true, signal:ui.listenerAbort.signal});
            window.addEventListener('keydown', event => {
                if (event.key === 'Escape' && ui.host.classList.contains('bc-open')) {
                    ui.escapeDismissed = true;
                    event.preventDefault(); event.stopImmediatePropagation(); close();
                }
            }, {capture:true, signal:ui.listenerAbort.signal});
            window.addEventListener('keyup', event => {
                if (event.key === 'Escape' && ui.escapeDismissed) {
                    event.preventDefault(); event.stopImmediatePropagation();
                    ui.escapeDismissed = false;
                }
            }, {capture:true, signal:ui.listenerAbort.signal});
        }
        const fullElement = document.fullscreenElement;
        const mount = fullElement && player.contains(fullElement)
            && fullElement.tagName !== 'VIDEO' ? fullElement : player;
        if (ui.host.parentElement !== mount) mount.appendChild(ui.host);
        ui.updateEdgeArea();
        ui.fullscreen = state.fullscreen;
        ui.inRoom = state.inRoom;
        ui.dockedChat = state.dockedChat;
        ui.host.classList.toggle('bc-docked', state.dockedChat);
        ui.host.style.display = state.inRoom ? 'block' : 'none';
        ui.host.classList.toggle('bc-fullscreen', state.fullscreen && state.inRoom);
        if (!state.inRoom) {
            clearTimeout(ui.closeTimer);
            ui.closeTimer = null;
            ui.editing = false;
            ui.composing = false;
            ui.host.classList.remove('bc-open');
            ui.edge.setAttribute('aria-expanded', 'false');
            ui.input.blur();
            if (typeof invokeCSharpAction === 'function') {
                invokeCSharpAction(JSON.stringify({source:'biliCinemaChat',
                    type:'panelState',open:false}));
            }
        }
        if (state.dockedChat && ui.host.classList.contains('bc-open')) ui.closePanel();
        ui.count.textContent = `${state.memberCount} 人`;
        if (ui.revision !== state.revision) {
            ui.revision = state.revision;
            ui.list.replaceChildren();
            if (state.messages.length === 0) {
                const empty = document.createElement('div'); empty.className = 'bc-empty';
                empty.textContent = '房间里还没有消息'; ui.list.appendChild(empty);
            }
            for (const message of state.messages) {
                const row = document.createElement('div');
                row.className = 'bc-message' + (message.role === 'guest' ? ' bc-guest' : '')
                    + (message.isSelf ? ' bc-self' : '');
                const avatar = document.createElement('div'); avatar.className = 'bc-avatar';
                avatar.textContent = message.nickname.slice(0, 1);
                const main = document.createElement('div'); main.className = 'bc-message-main';
                const meta = document.createElement('div'); meta.className = 'bc-meta';
                const name = document.createElement('span'); name.className = 'bc-name';
                name.textContent = message.isSelf ? `${message.nickname} · 我` : message.nickname;
                const time = document.createElement('time'); time.className = 'bc-time';
                time.textContent = new Date(message.sentAtUnixMs).toLocaleTimeString('zh-CN', {hour:'2-digit',minute:'2-digit'});
                const body = document.createElement('div'); body.className = 'bc-body';
                body.textContent = message.text;
                meta.append(name, time); main.append(meta, body); row.append(avatar, main);
                ui.list.appendChild(row);
            }
            ui.list.scrollTop = ui.list.scrollHeight;
        }
        if (ui.toastSequence !== state.toastSequence) {
            ui.toastSequence = state.toastSequence;
            clearTimeout(ui.toastTimer);
            ui.toast.classList.remove('bc-visible');
            if (state.inRoom && state.toast) {
                ui.toastName.textContent = state.toast.nickname;
                ui.toastBody.textContent = state.toast.text;
                ui.toast.classList.add('bc-visible');
                ui.toastTimer = setTimeout(() => ui.toast.classList.remove('bc-visible'),
                    state.toastRemainingMilliseconds);
            }
        }
        if (ui.actionSequence !== state.hostActionToastSequence) {
            ui.actionSequence = state.hostActionToastSequence;
            for (const timer of ui.actionTimers) clearTimeout(timer);
            ui.actionTimers = [];
            ui.actionToasts.replaceChildren();
            if (state.inRoom) {
                for (const notice of state.hostActionNotices) {
                    const row = make('div', 'bc-action-toast');
                    row.textContent = notice.text;
                    ui.actionToasts.appendChild(row);
                    ui.actionTimers.push(setTimeout(() => row.remove(), notice.remainingMilliseconds));
                }
            }
        }
        return true;
        })()
        """;
}
