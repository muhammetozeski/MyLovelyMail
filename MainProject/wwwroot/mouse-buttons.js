// Takes Back and Forward away from the mouse's side buttons and gives them something to do.
//
// A gaming mouse's buttons 4 and 5 are wired to browser navigation. Inside a one-window mail
// client there is nowhere to navigate back TO, so the default behavior can only land somewhere
// the user did not ask for. The same goes for Backspace and Alt+Arrow.
//
// What a button DOES is declared in markup: an element carries data-mouse4 / data-mouse5 with an
// action name, and optionally data-mouse4-value with the payload (the visible text is the payload
// when the attribute is absent). Nothing is hard-coded per element here.
//
// The fallback covers the general case the app cannot annotate one by one: if the element under
// the cursor holds nothing but an e-mail address, button 4 copies that address.
(function () {
    "use strict";

    var BACK = 3, FORWARD = 4;
    var ADDRESS = /^[^\s@<>()[\]\\,;:]+@[^\s@<>()[\]\\,;:]+\.[^\s@<>()[\]\\,;:]+$/;

    function attributeFor(button) {
        return button === BACK ? "data-mouse4" : "data-mouse5";
    }

    function declared(target, button) {
        var attribute = attributeFor(button);
        var node = target && target.closest ? target.closest("[" + attribute + "]") : null;
        if (!node) return null;
        var value = node.getAttribute(attribute + "-value");
        if (value === null) value = (node.textContent || "").trim();
        return { action: node.getAttribute(attribute), value: value };
    }

    // Only the address itself, never a line that happens to contain one: "copied the box you
    // clicked" has to mean exactly what it says.
    function addressUnder(target) {
        var node = target;
        for (var depth = 0; node && depth < 3; depth++, node = node.parentElement) {
            var text = (node.textContent || "").trim();
            if (ADDRESS.test(text)) return text;
        }
        return null;
    }

    function isSideButton(event) {
        return event.button === BACK || event.button === FORWARD;
    }

    function swallow(event) {
        if (isSideButton(event)) {
            event.preventDefault();
            event.stopPropagation();
        }
    }

    // Both halves of the press: WebView2 decides to navigate on one of them depending on version.
    document.addEventListener("mousedown", swallow, true);
    document.addEventListener("mouseup", swallow, true);

    document.addEventListener("auxclick", function (event) {
        if (!isSideButton(event)) return;
        event.preventDefault();
        event.stopPropagation();

        var hit = declared(event.target, event.button);
        if (!hit || !hit.action) {
            if (event.button !== BACK) return;
            var address = addressUnder(event.target);
            if (!address) return;
            hit = { action: "copy", value: address };
        }

        try {
            DotNet.invokeMethodAsync("MainProject", "MouseSideButton", hit.action, hit.value);
        } catch (error) {
            // Blazor not up yet: the click simply does nothing, which is still better than
            // navigating away.
        }
    }, true);

    document.addEventListener("keydown", function (event) {
        var target = event.target || {};
        var typing = /^(INPUT|TEXTAREA)$/.test(target.tagName || "") || target.isContentEditable;
        if (event.key === "Backspace" && !typing) event.preventDefault();
        if (event.altKey && (event.key === "ArrowLeft" || event.key === "ArrowRight")) event.preventDefault();
    }, true);
})();
