// One input router for the app and its owned previews. Mail commands are declared in markup;
// editing controls and otherwise unassigned surfaces receive useful fallbacks.
(function () {
    "use strict";
    const BACK = 3, FORWARD = 4;
    const script = document.currentScript;
    const previewToken = script && script.getAttribute("data-input-token");
    const preview = !!previewToken && window.parent !== window;
    const boundDocuments = new WeakSet();
    let contextTarget = null, contextDocument = document;

    // Remove the opaque preview's capability before user code runs. Its trusted-event listeners
    // retain it in this closure; synthetic events and arbitrary preview messages are refused.
    if (preview) script.remove();

    function sideButton(event) { return event.button === BACK || event.button === FORWARD; }
    function closest(target, selector) { return target && target.closest ? target.closest(selector) : null; }
    function editable(target) { return closest(target, "input,textarea,select,[contenteditable='true']"); }
    function sensitive(target) { return target && target.type === "password"; }

    async function invoke(method, ...args) {
        try { return await DotNet.invokeMethodAsync("MainProject", method, ...args); }
        catch (error) { console.warn("Input action could not reach the application", method, error); }
    }

    function selectionOf(doc, target) {
        if (sensitive(target)) return "";
        if (target && typeof target.value === "string")
            return typeof target.selectionStart === "number" ? target.value.slice(target.selectionStart, target.selectionEnd) : "";
        return String((doc.getSelection && doc.getSelection()) || "");
    }

    function describe(event, doc, frame) {
        const target = event.target;
        const field = editable(target);
        const link = closest(target, "a[href]");
        const rect = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
        const control = closest(target, "button,[role='button'],label");
        const row = closest(target, ".settings-row");
        return {
            x: rect.left + event.clientX, y: rect.top + event.clientY,
            text: sensitive(field) ? "" : field && typeof field.value === "string" ? field.value : (target.textContent || "").trim(),
            selection: selectionOf(doc, field || target), link: link ? link.href : "",
            messagePayload: frame ? frame.getAttribute("data-message-targets") || "" : "",
            primaryPayload: frame ? frame.getAttribute("data-message-primary") || "" : "",
            isMailContent: !!frame && frame.matches(".mail-reader-frame"),
            canActivate: !!control && !control.disabled,
            canSelect: !sensitive(field),
            canReset: !!row && !!row.querySelector(".setting-editor-reset:not(:disabled)")
        };
    }

    function postPreview(event, kind) {
        if (!event.isTrusted) return;
        window.parent.postMessage({ source: "lovely-preview-input", token: previewToken,
            kind: kind, button: event.button, request: describe(event, document, null) }, "*");
    }

    async function showContext(event, doc, frame) {
        contextTarget = frame ? doc.body : event.target;
        contextDocument = doc;
        const annotated = !frame && closest(event.target, "[data-context-menu]");
        if (annotated) {
            annotated.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true,
                clientX: event.clientX, clientY: event.clientY }));
            return;
        }
        await invoke("MouseContextMenu", describe(event, doc, frame));
    }

    function selectText(target, doc) {
        if (!target) return false;
        if (target.select) {
            target.focus();
            target.select();
        } else {
            const range = doc.createRange();
            range.selectNodeContents(target);
            const selection = doc.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
        }
        return true;
    }

    function declared(target, button, frame) {
        const attribute = button === BACK ? "data-mouse4" : "data-mouse5";
        const node = closest(target, "[" + attribute + "]") || frame && closest(frame, "[" + attribute + "]");
        if (!node) return null;
        const value = node.getAttribute(attribute + "-value");
        return { node: node, action: node.getAttribute(attribute),
            selector: node.getAttribute(attribute + "-selector"),
            value: value === null ? (node.textContent || "").trim() : value };
    }

    async function handleSide(event, doc, frame) {
        const target = event.target;
        const field = editable(target);
        const vault = closest(target, ".vault-overlay");
        if (vault) {
            const password = vault.querySelector("input[type='password']");
            const submit = vault.querySelector("button:not(:disabled)");
            if (event.button === FORWARD && submit && !field) submit.click();
            else selectText(field || password, doc);
            return;
        }
        if (field && !frame) {
            if (event.button === FORWARD && ["checkbox", "radio", "file"].includes(field.type)) field.click();
            else if (event.button === FORWARD || sensitive(field)) selectText(field, doc);
            else await invoke("MouseSideButton", "copy", selectionOf(doc, field) || field.value || field.textContent || "");
            return;
        }
        const hit = declared(target, event.button, frame);
        const control = closest(target, "button,[role='button']");
        if (event.button === FORWARD && !frame && control && !control.disabled
            && (!hit || hit.node !== control && hit.node.contains(control))) {
            control.click();
            return;
        }
        if (hit && hit.action === "click") {
            const actionTarget = hit.selector ? hit.node.querySelector(hit.selector) : hit.node;
            if (actionTarget && !actionTarget.disabled) actionTarget.click();
            return;
        }
        if (hit && hit.action === "menu") { await showContext(event, doc, frame); return; }
        if (hit && hit.action && hit.action !== "menu") {
            await invoke("MouseSideButton", hit.action, hit.value);
            return;
        }
        if (event.button === BACK) await showContext(event, doc, frame);
        else if (control && !control.disabled) control.click();
        else await invoke("MouseSideButton", "sync", "");
    }

    function attachDocument(doc, frame) {
        if (!doc || boundDocuments.has(doc)) return;
        boundDocuments.add(doc);
        function swallow(event) {
            if (!sideButton(event)) return;
            event.preventDefault();
            event.stopPropagation();
        }
        doc.addEventListener("mousedown", swallow, true);
        doc.addEventListener("mouseup", swallow, true);
        doc.addEventListener("auxclick", async function (event) {
            if (!sideButton(event)) return;
            swallow(event);
            if (preview) { postPreview(event, "side"); return; }
            try { await handleSide(event, doc, frame); }
            catch (error) { console.warn("Mouse side-button action failed", error); }
        }, true);
        doc.addEventListener("contextmenu", function (event) {
            if (!frame && !preview && editable(event.target)) { event.stopPropagation(); return; }
            if (!frame && !preview && closest(event.target, "[data-context-menu]")) return;
            event.preventDefault();
            event.stopPropagation();
            if (preview) postPreview(event, "menu");
            else showContext(event, doc, frame);
        }, true);
        doc.addEventListener("keydown", function (event) {
            const typing = editable(event.target);
            if (!typing && event.ctrlKey && event.key.toLowerCase() === "a" && closest(event.target, ".mail-rows")) event.preventDefault();
            if (event.key === "Backspace" && !typing) event.preventDefault();
            if (event.altKey && (event.key === "ArrowLeft" || event.key === "ArrowRight")) event.preventDefault();
        }, true);
    }

    if (!preview) {
        window.lovelyMouse = {
            attachFrame: function (frame) {
                if (!frame.matches(".mail-reader-frame")) return;
                try { attachDocument(frame.contentDocument, frame); }
                catch (error) { console.warn("Could not attach mail-frame input", error); }
            },
            useContext: function (operation) {
                if (!contextTarget || !contextTarget.isConnected) return false;
                const field = editable(contextTarget);
                if (operation === "select") return selectText(field || contextTarget, contextDocument);
                const control = operation === "reset"
                    ? closest(contextTarget, ".settings-row")?.querySelector(".setting-editor-reset:not(:disabled)")
                    : closest(contextTarget, "button,[role='button'],label");
                if (!control || control.disabled) return false;
                control.click();
                return true;
            },
            positionMenu: function (element, x, y, margin) {
                let rect = element.getBoundingClientRect();
                const scale = rect.width / element.offsetWidth || 1;
                if (rect.width > window.innerWidth - margin * 2) element.style.maxWidth = (window.innerWidth - margin * 2) / scale + "px";
                if (rect.height > window.innerHeight - margin * 2) element.style.maxHeight = (window.innerHeight - margin * 2) / scale + "px";
                rect = element.getBoundingClientRect();
                element.style.left = Math.max(margin, Math.min(x, window.innerWidth - rect.width - margin)) / scale + "px";
                element.style.top = Math.max(margin, Math.min(y, window.innerHeight - rect.height - margin)) / scale + "px";
            }
        };
        window.addEventListener("message", function (event) {
            const data = event.data;
            if (!data || data.source !== "lovely-preview-input") return;
            const frame = Array.from(document.querySelectorAll("iframe.signature-editor-preview"))
                .find(frame => frame.contentWindow === event.source && frame.getAttribute("data-input-token") === data.token);
            if (!frame || !data.request || !["side", "menu"].includes(data.kind)) return;
            const rect = frame.getBoundingClientRect();
            const request = { x: rect.left + Number(data.request.x || 0), y: rect.top + Number(data.request.y || 0),
                text: String(data.request.text || ""), selection: String(data.request.selection || ""),
                link: String(data.request.link || ""), canActivate: false, canSelect: false, canReset: false };
            contextTarget = frame;
            contextDocument = document;
            if (data.kind === "menu" || data.button === BACK) invoke("MouseContextMenu", request);
            else if (data.button === FORWARD) invoke("MouseSideButton", "sync", "");
        });
        for (const frame of document.querySelectorAll("iframe.mail-reader-frame")) window.lovelyMouse.attachFrame(frame);
    }
    attachDocument(document, null);
})();
