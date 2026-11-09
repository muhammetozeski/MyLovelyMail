// Shared mouse-button, context-menu, Escape-cancel drag, iframe auto-fit, and double-precision column splitter wiring.
(function () {
    if (window.__myLovelyMailMouseButtonsLoaded) return;
    window.__myLovelyMailMouseButtonsLoaded = true;

    const assembly = "MyLovelyMail";
    let activeInput = null;
    let sidebarDragRef = null;
    window.__mailDragCancelled = false;

    function isEditableInput(element) {
        if (!element || element.disabled || element.readOnly) return null;
        if (element.tagName === "TEXTAREA") return element;
        if (element.tagName !== "INPUT") return null;
        const type = (element.type || "text").toLowerCase();
        if (type === "password" || type === "checkbox" || type === "radio" || type === "button" || type === "submit" || type === "file" || type === "color" || type === "range")
            return null;
        return element;
    }

    function selectedTextIn(element) {
        const input = isEditableInput(element);
        if (input && typeof input.selectionStart === "number" && typeof input.selectionEnd === "number" && input.selectionEnd > input.selectionStart)
            return input.value.substring(input.selectionStart, input.selectionEnd).trim();
        return (window.getSelection ? String(window.getSelection()) : "").trim();
    }

    function cleanText(value) {
        return (value || "").replace(/\s+/g, " ").trim().slice(0, 4000);
    }

    function invokeSideButton(action, payload) {
        if (!window.DotNet || !action) return;
        window.DotNet.invokeMethodAsync(assembly, "MouseSideButton", action, payload || "");
    }

    document.addEventListener("mouseup", function (event) {
        if (event.button !== 3 && event.button !== 4) return;
        const attr = event.button === 3 ? "data-mouse4" : "data-mouse5";
        const valAttr = event.button === 3 ? "data-mouse4-value" : "data-mouse5-value";
        const selAttr = event.button === 3 ? "data-mouse4-selector" : "data-mouse5-selector";
        const host = event.target && event.target.closest ? event.target.closest("[" + attr + "]") : null;
        if (!host) return;
        event.preventDefault();
        event.stopPropagation();
        const act = host.getAttribute(attr);
        if (act === "click") {
            const sel = host.getAttribute(selAttr);
            const btn = sel ? host.querySelector(sel) : host;
            if (btn && typeof btn.click === "function") btn.click();
            return;
        }
        if (act === "menu") {
            host.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: event.clientX, clientY: event.clientY }));
            return;
        }
        invokeSideButton(act, host.getAttribute(valAttr) || "");
    }, true);

    document.addEventListener("contextmenu", function (event) {
        if (!window.DotNet || event.shiftKey) return;
        const target = event.target && event.target.nodeType === 1 ? event.target : event.target && event.target.parentElement;
        if (!target || target.closest(".app-context-backdrop") || target.closest("[data-context-menu='true']")) return;
        if (target.closest("input[type='password']")) return;

        activeInput = isEditableInput(target.closest("input,textarea"));
        const link = target.closest("a[href]");
        const actionable = target.closest("button,a[href],[role='button'],input,select,textarea,summary");
        const messageHost = target.closest("[data-message-targets],[data-mail-messages]");
        const textHost = target.closest("[data-context-text]") || messageHost || actionable || target;
        const selection = cleanText(selectedTextIn(activeInput || target));
        const explicitText = cleanText(textHost && textHost.getAttribute ? textHost.getAttribute("data-context-text") : "");
        const fallbackText = activeInput ? cleanText(activeInput.value) : cleanText(textHost ? textHost.innerText || textHost.textContent : "");

        event.preventDefault();
        event.stopPropagation();
        window.DotNet.invokeMethodAsync(assembly, "MouseContextMenu", {
            x: event.clientX,
            y: event.clientY,
            text: explicitText || selection || fallbackText,
            selection: selection,
            link: link ? link.href || "" : "",
            messagePayload: messageHost ? (messageHost.getAttribute("data-message-targets") || messageHost.getAttribute("data-mail-messages") || "") : "",
            primaryPayload: messageHost ? (messageHost.getAttribute("data-message-primary") || messageHost.getAttribute("data-mail-primary") || "") : "",
            isMailContent: Boolean(target.closest(".mail-reader-body,.mail-reader-header-card,.mail-row")),
            canActivate: Boolean(actionable && !activeInput && !actionable.disabled),
            canSelect: Boolean(activeInput || fallbackText || explicitText),
            canReset: Boolean(activeInput && activeInput.value !== activeInput.defaultValue)
        });
    }, true);

    // Pressing Escape while dragging cancels the drag-and-drop reorder before releasing the mouse button.
    document.addEventListener("dragstart", function () {
        window.__mailDragCancelled = false;
    }, true);

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            window.__mailDragCancelled = true;
            if (sidebarDragRef) {
                try { sidebarDragRef.invokeMethodAsync("CancelActiveDragFromJs"); } catch { }
            }
        }
    }, true);

    window.myLovelyMailSidebarDrag = {
        register: function (dotNetRef) { sidebarDragRef = dotNetRef; },
        unregister: function () { sidebarDragRef = null; }
    };

    window.myLovelyMailContextActions = {
        selectText: function () {
            if (activeInput && typeof activeInput.select === "function") {
                activeInput.focus();
                activeInput.select();
                return true;
            }
            return false;
        },
        resetInput: function () {
            if (!activeInput) return false;
            activeInput.value = activeInput.defaultValue || "";
            activeInput.dispatchEvent(new Event("input", { bubbles: true }));
            activeInput.dispatchEvent(new Event("change", { bubbles: true }));
            return true;
        }
    };

    // Auto-wire reader iframe height (110% headroom + overflow-y:hidden + wheel forwarding) whenever .mail-reader-frame appears.
    const wireReaderIframe = (frame) => {
        if (!frame || frame.__mlmHostWired) return;
        frame.__mlmHostWired = true;
        if (!frame.id) frame.id = "mail-reader-frame-auto";
        const runFit = () => {
            if (window.mailReader && typeof window.mailReader.fit === "function") {
                window.mailReader.fit(frame.id);
            }
        };
        frame.addEventListener("load", runFit);
        runFit();
    };

    if (typeof MutationObserver !== "undefined") {
        const mo = new MutationObserver(() => {
            const frame = document.querySelector("iframe.mail-reader-frame");
            if (frame) wireReaderIframe(frame);
        });
        if (document.body) {
            mo.observe(document.body, { childList: true, subtree: true });
        } else {
            window.addEventListener("DOMContentLoaded", () => {
                mo.observe(document.body, { childList: true, subtree: true });
            });
        }
    }

    // Resizable 3-column splitters with IEEE 754 double precision and +-3.0 percentage-point snap to defaults.
    window.myLovelyMailSplitters = {
        startDrag: function (dotNetRef, splitterIndex, startClientX, initialSidebarPct, initialListPct) {
            const screen = document.querySelector(".mail-screen");
            if (!screen) return;

            const rect = screen.getBoundingClientRect();
            const totalWidth = Math.max(rect.width, 300.0);
            const snapWindow = 3.0;
            const defSidebar = 20.0;
            const defList = 32.0;
            const defReader = 48.0;

            const startSidebar = Number(initialSidebarPct || 20.0);
            const startList = Number(initialListPct || 32.0);
            const startReader = Math.max(22.0, 100.0 - startSidebar - startList);

            let curSidebar = startSidebar;
            let curList = startList;
            let curReader = startReader;

            const applyCss = (s, l, r) => {
                screen.style.setProperty("--col-sidebar-pct", s + "%");
                screen.style.setProperty("--col-list-pct", l + "%");
                screen.style.setProperty("--col-reader-pct", r + "%");
            };

            const onMove = (moveEvent) => {
                const deltaPx = Number(moveEvent.clientX) - Number(startClientX);
                const deltaPct = (deltaPx / totalWidth) * 100.0;

                if (splitterIndex === 1) {
                    const totalFirstTwo = startSidebar + startList;
                    let rawSidebar = Math.min(Math.max(startSidebar + deltaPct, 12.0), totalFirstTwo - 16.0);
                    if (Math.abs(rawSidebar - defSidebar) <= snapWindow && (totalFirstTwo - defSidebar) >= 16.0) {
                        curSidebar = defSidebar;
                        curList = totalFirstTwo - defSidebar;
                    } else {
                        curSidebar = rawSidebar;
                        curList = totalFirstTwo - rawSidebar;
                    }
                    curReader = 100.0 - curSidebar - curList;
                } else {
                    const totalLastTwo = startList + startReader;
                    let rawList = Math.min(Math.max(startList + deltaPct, 16.0), totalLastTwo - 22.0);
                    let rawReader = totalLastTwo - rawList;
                    if (Math.abs(rawReader - defReader) <= snapWindow && (totalLastTwo - defReader) >= 16.0) {
                        curReader = defReader;
                        curList = totalLastTwo - defReader;
                    } else if (Math.abs(rawList - defList) <= snapWindow && (totalLastTwo - defList) >= 22.0) {
                        curList = defList;
                        curReader = totalLastTwo - defList;
                    } else {
                        curList = rawList;
                        curReader = rawReader;
                    }
                    curSidebar = 100.0 - curList - curReader;
                }
                applyCss(curSidebar, curList, curReader);
            };

            const cleanup = () => {
                window.removeEventListener("pointermove", onMove, true);
                window.removeEventListener("pointerup", onUp, true);
                window.removeEventListener("keydown", onKey, true);
                document.body.style.userSelect = "";
                document.body.style.cursor = "";
            };

            const onUp = () => {
                cleanup();
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync("UpdateColumnSplitFromJs", curSidebar, curList, true);
                }
            };

            const onKey = (keyEvent) => {
                if (keyEvent.key === "Escape") {
                    keyEvent.preventDefault();
                    cleanup();
                    applyCss(startSidebar, startList, startReader);
                }
            };

            document.body.style.userSelect = "none";
            document.body.style.cursor = "col-resize";
            applyCss(curSidebar, curList, curReader);
            window.addEventListener("pointermove", onMove, true);
            window.addEventListener("pointerup", onUp, true);
            window.addEventListener("keydown", onKey, true);
        }
    };
})();
