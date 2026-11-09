// Gives every reader iframe 110% of the message height inside it with overflow-y: hidden
// and forwards mouse-wheel scrolling to the outer scroll container so the iframe never
// shows its own inner vertical scrollbar.
(function () {
    "use strict";

    var SELECTOR = "iframe.mail-reader-frame";
    var watched = new WeakSet();
    var resizeWatchers = new WeakMap();

    function fit(frame) {
        try {
            var doc = frame.contentDocument;
            if (!doc || !doc.body || !doc.documentElement) return;
            doc.documentElement.style.overflowY = "hidden";
            doc.body.style.overflowY = "hidden";
            var rawHeight = Math.max(
                doc.body.scrollHeight,
                doc.documentElement.scrollHeight,
                doc.body.offsetHeight,
                doc.documentElement.offsetHeight
            );
            if (rawHeight > 0) {
                var paddedHeight = Math.ceil(rawHeight * 1.10) + 16;
                frame.style.height = Math.max(paddedHeight, 220) + "px";
            }
            if (!doc.__mlmWheelWired) {
                doc.__mlmWheelWired = true;
                doc.addEventListener("wheel", function (event) {
                    if (event.ctrlKey) return;
                    var scroller = frame.closest(".mail-reader") || document.scrollingElement || document.documentElement;
                    if (scroller) {
                        scroller.scrollBy({ top: event.deltaY, left: event.deltaX, behavior: "auto" });
                    }
                }, { passive: true });
            }
        } catch (error) {
            // Frame torn down mid-measure, or a document we may not read: the CSS height stands.
        }
    }

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
            if (window.lovelyMouse) window.lovelyMouse.attachFrame(frame);
        });
        fit(frame);
        followContent(frame);
        if (window.lovelyMouse) window.lovelyMouse.attachFrame(frame);
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

    window.mailReader = {
        fit: function (frameId) {
            var frame = frameId ? document.getElementById(frameId) : document.querySelector(SELECTOR);
            if (frame) fit(frame);
        },
        print: function (frameId) {
            var frame = frameId ? document.getElementById(frameId) : document.querySelector(SELECTOR);
            if (frame && frame.contentWindow) {
                try {
                    frame.contentWindow.focus();
                    frame.contentWindow.print();
                    return;
                } catch (e) { }
            }
            window.print();
        }
    };
})();
