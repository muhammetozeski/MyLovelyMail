// Minimal WebView2 browser-navigation guard and <iframe srcdoc> event forwarder.
// All UI buttons, menus, drag-and-drop, and mouse actions run natively in Blazor C#.
(function () {
    "use strict";

    const BACK = 3, FORWARD = 4;
    const script = document.currentScript;
    const previewToken = script && script.getAttribute("data-input-token");
    const isPreviewFrame = !!previewToken && window.parent !== window;
    const boundDocuments = new WeakSet();

    if (isPreviewFrame) script.remove();

    function isSideButton(event) { return event.button === BACK || event.button === FORWARD; }
    function closest(target, selector) { return target && target.closest ? target.closest(selector) : null; }
    function isEditable(target) { return !!closest(target, "input,textarea,select,[contenteditable='true']"); }

    async function invokeDotNet(method, ...args) {
        try { await DotNet.invokeMethodAsync("MainProject", method, ...args); }
        catch (error) { console.warn("Frame bridge could not reach MainProject", method, error); }
    }

    function buildFrameContextRequest(event, doc, frame) {
        const link = closest(event.target, "a[href],area[href],a[data-mlm-link]");
        const rect = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
        const selected = String((doc.getSelection && doc.getSelection()) || "");
        return {
            x: rect.left + event.clientX,
            y: rect.top + event.clientY,
            text: "",
            selection: selected,
            link: link ? (link.getAttribute("data-mlm-link") || link.href || "") : "",
            messagePayload: frame ? (frame.getAttribute("data-message-targets") || "") : "",
            primaryPayload: frame ? (frame.getAttribute("data-message-primary") || "") : "",
            isMailContent: !!frame && frame.matches(".mail-reader-frame"),
            canActivate: false,
            canSelect: false,
            canReset: false
        };
    }

    function attachFrameDocument(doc, frame) {
        if (!doc || boundDocuments.has(doc)) return;
        boundDocuments.add(doc);

        function blockAnonymousLinks(event) {
            if (frame && frame.getAttribute("data-anonymous") === "true" && closest(event.target, "a,area")) {
                event.preventDefault();
                event.stopPropagation();
            }
        }

        doc.addEventListener("click", blockAnonymousLinks, true);
        doc.addEventListener("auxclick", blockAnonymousLinks, true);

        doc.addEventListener("mousedown", function (event) {
            if (isSideButton(event)) event.preventDefault();
        }, true);

        doc.addEventListener("mouseup", function (event) {
            if (isSideButton(event)) event.preventDefault();
        }, true);

        doc.addEventListener("auxclick", function (event) {
            if (!isSideButton(event)) return;
            event.preventDefault();
            event.stopPropagation();
            if (isPreviewFrame) {
                if (event.isTrusted) {
                    window.parent.postMessage({ source: "lovely-preview-input", token: previewToken, kind: "side", button: event.button }, "*");
                }
                return;
            }
            if (frame) {
                const payload = frame.getAttribute("data-message-targets") || "";
                if (payload.length > 0) {
                    invokeDotNet("MouseSideButton", event.button === BACK ? "Trash" : "Read", payload);
                }
            }
        }, true);

        doc.addEventListener("contextmenu", function (event) {
            event.preventDefault();
            event.stopPropagation();
            if (isPreviewFrame) {
                if (event.isTrusted) {
                    window.parent.postMessage({ source: "lovely-preview-input", token: previewToken, kind: "menu" }, "*");
                }
                return;
            }
            if (frame) {
                invokeDotNet("MouseContextMenu", buildFrameContextRequest(event, doc, frame));
            }
        }, true);
    }

    if (isPreviewFrame) {
        attachFrameDocument(document, null);
        return;
    }

    // Main Blazor document: only prevent WebView2's built-in browser Back/Forward/ContextMenu defaults
    // WITHOUT stopping propagation, so Blazor C# @onmouseup / @oncontextmenu / @onkeydown receive every event.
    document.addEventListener("mousedown", function (event) {
        if (isSideButton(event)) event.preventDefault();
    }, true);

    document.addEventListener("mouseup", function (event) {
        if (isSideButton(event)) event.preventDefault();
    }, true);

    document.addEventListener("auxclick", function (event) {
        if (isSideButton(event)) event.preventDefault();
    }, true);

    document.addEventListener("contextmenu", function (event) {
        if (!isEditable(event.target)) event.preventDefault();
    }, false);

    document.addEventListener("keydown", function (event) {
        const typing = isEditable(event.target);
        if (!typing && event.ctrlKey && event.key.toLowerCase() === "a" && closest(event.target, ".mail-rows")) event.preventDefault();
        if (event.key === "Backspace" && !typing) event.preventDefault();
        if (event.altKey && (event.key === "ArrowLeft" || event.key === "ArrowRight")) event.preventDefault();
    }, true);

    window.addEventListener("message", function (event) {
        const data = event.data;
        if (!data || data.source !== "lovely-preview-input") return;
        const frame = Array.from(document.querySelectorAll("iframe.signature-editor-preview"))
            .find(f => f.contentWindow === event.source && f.getAttribute("data-input-token") === data.token);
        if (frame && data.kind === "side" && data.button === FORWARD) {
            invokeDotNet("MouseSideButton", "sync", "");
        }
    });

    window.lovelyMouse = {
        attachFrame: function (frame) {
            if (!frame || !frame.matches(".mail-reader-frame")) return;
            try { attachFrameDocument(frame.contentDocument, frame); }
            catch (error) { console.warn("Could not attach mail-frame input", error); }
        }
    };

    for (const frame of document.querySelectorAll("iframe.mail-reader-frame")) {
        window.lovelyMouse.attachFrame(frame);
    }
})();
