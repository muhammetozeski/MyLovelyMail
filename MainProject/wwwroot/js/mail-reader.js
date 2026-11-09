// Helpers for the sandboxed mail-reader <iframe srcdoc="...">.
// Uses 110% height headroom + overflow-y:hidden + ResizeObserver + wheel forwarding so the iframe
// never spawns an inner vertical scrollbar when scrolling over the email body.
window.mailReader = {
    fit: function (frameId) {
        const frame = document.getElementById(frameId);
        if (!frame) return;

        const measureAndApply = () => {
            try {
                const doc = frame.contentDocument;
                if (!doc || !doc.body || !doc.documentElement) return;
                doc.documentElement.style.overflowY = "hidden";
                doc.body.style.overflowY = "hidden";
                const rawHeight = Math.max(
                    doc.body.scrollHeight,
                    doc.documentElement.scrollHeight,
                    doc.body.offsetHeight,
                    doc.documentElement.offsetHeight
                );
                const paddedHeight = Math.ceil(rawHeight * 1.10) + 16;
                frame.style.height = Math.max(paddedHeight, 220) + "px";
            } catch {
                // Cross-origin frame (should not happen with srcdoc); keep the CSS min-height.
            }
        };

        const wireFrameDocument = () => {
            measureAndApply();
            try {
                const doc = frame.contentDocument;
                if (!doc || !doc.body) return;

                if (!doc.__mlmWired) {
                    doc.__mlmWired = true;
                    doc.addEventListener("wheel", (event) => {
                        const scroller = frame.closest(".mail-reader");
                        if (scroller && !event.ctrlKey) {
                            scroller.scrollBy({ top: event.deltaY, left: event.deltaX, behavior: "auto" });
                        }
                    }, { passive: true });

                    doc.addEventListener("toggle", () => setTimeout(measureAndApply, 10), true);
                    if (typeof ResizeObserver !== "undefined") {
                        const ro = new ResizeObserver(() => measureAndApply());
                        ro.observe(doc.body);
                    }
                }
                const images = doc.querySelectorAll("img");
                images.forEach(img => {
                    if (!img.complete) {
                        img.addEventListener("load", measureAndApply, { once: true });
                    }
                });
            } catch { }
        };

        frame.onload = wireFrameDocument;
        wireFrameDocument();
    },

    print: function (frameId) {
        const frame = document.getElementById(frameId);
        if (!frame || !frame.contentWindow) return;
        try {
            frame.contentWindow.focus();
            frame.contentWindow.print();
        } catch {
            window.print();
        }
    }
};
