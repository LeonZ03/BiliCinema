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
        int hostActionToastSequence)
    {
        var state = new
        {
            fullscreen,
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
            const host = document.createElement('div');
            host.id = 'bc-room-chat';
            const css = document.createElement('style');
            css.textContent = `
                #bc-room-chat { position:fixed!important; inset:0!important; z-index:2147483647!important;
                    color:#f8f9fa; font:14px/1.45 'Segoe UI','Microsoft YaHei',sans-serif;
                    pointer-events:none!important; }
                #bc-room-chat * { box-sizing:border-box; }
                #bc-room-chat .bc-edge { position:absolute; top:0; right:0; bottom:0; width:18px;
                    pointer-events:auto; cursor:pointer; }
                #bc-room-chat .bc-panel { position:absolute; top:0; right:0; bottom:0;
                    width:min(320px, 38vw); min-width:260px; display:flex; flex-direction:column;
                    background:rgba(52,54,56,.72); backdrop-filter:blur(14px) saturate(.75);
                    border-left:1px solid rgba(255,255,255,.16); box-shadow:-14px 0 34px rgba(0,0,0,.14);
                    transform:translateX(102%); opacity:0; pointer-events:none;
                    transition:transform .18s ease,opacity .18s ease; }
                #bc-room-chat.bc-fullscreen.bc-open .bc-panel { transform:none; opacity:1; pointer-events:auto; }
                #bc-room-chat:not(.bc-fullscreen) .bc-edge { display:none; }
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
                #bc-room-chat .bc-avatar { flex:0 0 27px; width:27px; height:27px;
                    display:flex; align-items:center; justify-content:center; border-radius:50%;
                    background:#8999a1; color:#fff; font-size:12px; font-weight:700; }
                #bc-room-chat .bc-message.bc-guest .bc-avatar { background:#99918e; }
                #bc-room-chat .bc-message-main { flex:1; min-width:0; }
                #bc-room-chat .bc-meta { display:flex; justify-content:space-between; gap:7px; margin-bottom:3px; }
                #bc-room-chat .bc-name { color:#e3ebee; font-size:12px; font-weight:650;
                    overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
                #bc-room-chat .bc-guest .bc-name { color:#e5e1de; }
                #bc-room-chat .bc-time { flex:0 0 auto; color:#c8cfd1; font-size:11px; }
                #bc-room-chat .bc-body { color:#fff; font-size:13px; white-space:pre-wrap;
                    overflow-wrap:anywhere; }
                #bc-room-chat .bc-compose { padding:12px 14px 16px;
                    border-top:1px solid rgba(255,255,255,.14); }
                #bc-room-chat .bc-input { width:100%; min-height:42px; max-height:112px; resize:vertical;
                    border-radius:9px; border:1px solid rgba(255,255,255,.33); outline:none;
                    padding:10px 11px; color:#fff; background:rgba(255,255,255,.125);
                    font:13px/1.45 'Segoe UI','Microsoft YaHei',sans-serif; }
                #bc-room-chat .bc-input:focus { border-color:#b5c7d0; box-shadow:0 0 0 2px rgba(181,199,208,.25); }
                #bc-room-chat .bc-input::placeholder { color:#d4d9da; }
                #bc-room-chat .bc-actions { display:flex; align-items:center; justify-content:space-between;
                    gap:7px; margin-top:7px; }
                #bc-room-chat .bc-hint { font-size:11px; color:#d4d9da; }
                #bc-room-chat .bc-send { min-width:72px; min-height:32px; padding:5px 12px;
                    border:1px solid rgba(255,255,255,.22); border-radius:8px; color:#fff;
                    background:#84929a; font-weight:600;
                    cursor:pointer; }
                #bc-room-chat .bc-send:hover { background:#91a0a8; }
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
            const panel = document.createElement('section'); panel.className = 'bc-panel';
            panel.id = 'bc-chat-panel';
            panel.setAttribute('aria-label', '房间聊天');
            const head = make('div', 'bc-head');
            const title = make('span', 'bc-title'); title.textContent = '房间聊天';
            const count = make('span', 'bc-count'); head.append(title, count);
            const list = make('div', 'bc-list');
            list.setAttribute('role', 'log'); list.setAttribute('aria-live', 'polite');
            const compose = make('div', 'bc-compose');
            const input = make('textarea', 'bc-input');
            input.maxLength = 500; input.placeholder = '说点什么…';
            input.setAttribute('aria-label', '聊天消息');
            const actions = make('div', 'bc-actions');
            const hint = make('span', 'bc-hint'); hint.textContent = 'Enter 发送 · Shift+Enter 换行';
            const sendButton = make('button', 'bc-send');
            sendButton.type = 'button'; sendButton.textContent = '发送';
            actions.append(hint, sendButton); compose.append(input, actions);
            panel.append(head, list, compose);
            const toast = document.createElement('div'); toast.className = 'bc-toast';
            toast.setAttribute('role', 'status');
            const toastName = make('div', 'bc-toast-name');
            const toastBody = make('div', 'bc-toast-body'); toast.append(toastName, toastBody);
            const actionToasts = make('div', 'bc-action-toasts');
            actionToasts.setAttribute('role', 'status');
            host.append(css, edge, panel, toast, actionToasts);
            ui = { host, edge, panel, list, input, count, toast, toastName, toastBody,
                actionToasts, revision:-1, toastSequence:-1, actionSequence:-1,
                fullscreen:false, inRoom:false, toastTimer:null, actionTimers:[],
                listenerAbort:new AbortController() };
            window.__biliCinemaChatUi = ui;
            const send = () => {
                const text = ui.input.value.trim();
                if (!text || text.length > 500 || !ui.inRoom) return;
                if (typeof invokeCSharpAction === 'function') {
                    invokeCSharpAction(JSON.stringify({source:'biliCinemaChat',type:'send',text}));
                    ui.input.value = '';
                    ui.input.focus();
                }
            };
            sendButton.addEventListener('click', send);
            ui.input.addEventListener('keydown', event => {
                if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
                    event.preventDefault(); send();
                }
            });
            for (const type of ['click','dblclick','pointerdown','pointerup','keydown','keyup','wheel']) {
                host.addEventListener(type, event => event.stopPropagation());
            }
            const setOpen = (open, focusInput) => {
                const wasOpen = ui.host.classList.contains('bc-open');
                ui.host.classList.toggle('bc-open', open);
                ui.edge.setAttribute('aria-expanded', open ? 'true' : 'false');
                if (!open) ui.input.blur();
                else if (!wasOpen && focusInput) ui.input.focus();
                if (wasOpen !== open && typeof invokeCSharpAction === 'function') {
                    invokeCSharpAction(JSON.stringify({source:'biliCinemaChat',
                        type:'panelState',open}));
                }
            };
            const close = () => setOpen(false, false);
            edge.addEventListener('keydown', event => {
                if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    setOpen(true, true);
                }
            });
            ui.input.addEventListener('keydown', event => {
                if (event.key === 'Escape') { event.preventDefault(); close(); }
            });
            window.addEventListener('pointermove', event => {
                if (!ui.fullscreen || !ui.inRoom) return;
                const panelLeft = window.innerWidth - ui.panel.getBoundingClientRect().width;
                const overPanel = ui.host.classList.contains('bc-open') && event.clientX >= panelLeft;
                if (event.clientX >= window.innerWidth - 18 || overPanel) {
                    setOpen(true, true);
                } else {
                    close();
                }
            }, {capture:true, signal:ui.listenerAbort.signal});
            document.addEventListener('mouseleave', close, {signal:ui.listenerAbort.signal});
            window.addEventListener('blur', close, {signal:ui.listenerAbort.signal});
        }
        const fullElement = document.fullscreenElement;
        const mount = fullElement && player.contains(fullElement)
            && fullElement.tagName !== 'VIDEO' ? fullElement : player;
        if (ui.host.parentElement !== mount) mount.appendChild(ui.host);
        ui.fullscreen = state.fullscreen;
        ui.inRoom = state.inRoom;
        ui.host.style.display = state.inRoom ? 'block' : 'none';
        ui.host.classList.toggle('bc-fullscreen', state.fullscreen && state.inRoom);
        if (!state.fullscreen || !state.inRoom) {
            ui.host.classList.remove('bc-open');
            ui.edge.setAttribute('aria-expanded', 'false');
            ui.input.blur();
            if (typeof invokeCSharpAction === 'function') {
                invokeCSharpAction(JSON.stringify({source:'biliCinemaChat',
                    type:'panelState',open:false}));
            }
        }
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
                row.className = 'bc-message' + (message.role === 'guest' ? ' bc-guest' : '');
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
