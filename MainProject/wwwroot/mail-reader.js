// Minimal DOM bridge for sandboxed <iframe srcdoc> mail bodies:
// measures content scrollHeight so the iframe never shows a nested scrollbar,
// and forwards vertical wheel events to the outer .mail-reader scroll container.
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
})();
