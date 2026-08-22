// Gives every reader iframe the height of the message inside it.
//
// An iframe is the one element that cannot size itself to its content, so the mail body used to
// be whatever height the window had left after the headers, the sender history, the rule traces
// and the attachment chips - on a message with a long trace that was almost nothing. Measured
// here, the mail opens whole and the page grows with it. The CSS bounds on .mail-reader-frame
// clamp the result, so an endless newsletter still stops somewhere.
//
// No Blazor interop on purpose: Blazor owns this DOM and rebuilds it on every message switch,
// so the script watches the document instead of waiting to be called. contentDocument is
// readable because the frame is sandboxed WITH allow-same-origin (and deliberately without
// allow-scripts, so nothing in the mail itself runs).
(function () {
    "use strict";

    var SELECTOR = "iframe.mail-reader-frame";
    var watched = new WeakSet();
    var resizeWatchers = new WeakMap();

    function fit(frame) {
        try {
            var doc = frame.contentDocument;
            if (!doc || !doc.body) return;
            var height = Math.max(doc.body.scrollHeight, doc.documentElement.scrollHeight);
            if (height > 0) frame.style.height = height + "px";
        } catch (error) {
            // Frame torn down mid-measure, or a document we may not read: the CSS height stands.
        }
    }

    // Images arrive after load and quoted blocks fold open on click - both change the height of a
    // document that is already showing, so one measurement at load time is not enough.
    function followContent(frame) {
        var previous = resizeWatchers.get(frame);
        if (previous) previous.disconnect();
        try {
            if (!window.ResizeObserver || !frame.contentDocument || !frame.contentDocument.body) return;
            var watcher = new ResizeObserver(function () { fit(frame); });
            watcher.observe(frame.contentDocument.body);
            resizeWatchers.set(frame, watcher);
        } catch (error) {
            // No observer: the load-time measurement is still in place.
        }
    }

    function watch(frame) {
        if (watched.has(frame)) return;
        watched.add(frame);
        frame.addEventListener("load", function () {
            fit(frame);
            followContent(frame);
        });
        // srcdoc may already have finished before the observer reached this element.
        fit(frame);
        followContent(frame);
    }

    function scan(node) {
        if (!node || node.nodeType !== 1) return;
        if (node.matches && node.matches(SELECTOR)) watch(node);
        if (node.querySelectorAll) {
            var frames = node.querySelectorAll(SELECTOR);
            for (var i = 0; i < frames.length; i++) watch(frames[i]);
        }
    }

    new MutationObserver(function (records) {
        for (var i = 0; i < records.length; i++) {
            var added = records[i].addedNodes;
            for (var j = 0; j < added.length; j++) scan(added[j]);
        }
    }).observe(document.documentElement, { childList: true, subtree: true });

    scan(document.documentElement);
})();
