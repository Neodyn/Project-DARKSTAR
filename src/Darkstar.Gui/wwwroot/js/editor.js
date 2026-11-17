// The config editor's only script: what Blazor cannot do by itself.
//
// - reveal(label, card): after the search opened a page, scroll to the field or card it found
//   and flash it, so the eye lands on it. Found by the text the panel shows - the same text the
//   search index holds (EditorSearch.cs) - inside the named card when there is one, since a label
//   like "MODULATION" or "REQUEST" appears more than once on a page. A field hidden behind a
//   switch that is off falls back to its card; nothing found leaves the page at its top.
// - Ctrl+K (Cmd+K) anywhere puts the cursor into the search box.
(function () {
    "use strict";

    function norm(text) {
        return (text || "").replace(/\s+/g, " ").trim().toLowerCase();
    }

    function findText(root, text) {
        var wanted = norm(text);
        if (!wanted) return null;

        var candidates = root.querySelectorAll("label, h1, span, div, strong");
        for (var i = 0; i < candidates.length; i++) {
            var element = candidates[i];
            // Only the element that holds the text itself, not every container around it.
            if (element.childElementCount > 0) continue;
            if (norm(element.textContent) === wanted) return element;
        }
        return null;
    }

    function flash(element) {
        var box = element.closest(".field, .toggle-row, .card") || element;
        box.scrollIntoView({ block: "center" });
        box.classList.remove("search-hit");
        void box.offsetWidth; // restart the animation when the same box is found twice in a row
        box.classList.add("search-hit");
        setTimeout(function () { box.classList.remove("search-hit"); }, 2400);
    }

    window.darkstarEditor = {
        reveal: function (label, card) {
            var content = document.querySelector(".content");
            if (!content) return false;

            var scope = content;
            var cardTitle = card ? findText(content, card) : null;
            if (cardTitle) scope = cardTitle.closest(".card") || content;

            var hit = findText(scope, label) || cardTitle || findText(content, label);
            if (hit) {
                flash(hit);
                return true;
            }

            content.scrollTop = 0;
            return false;
        }
    };

    document.addEventListener("keydown", function (e) {
        if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === "k" || e.key === "K")) {
            var box = document.getElementById("editor-search");
            if (box) {
                e.preventDefault();
                box.focus();
                box.select();
            }
        }
    });
})();
