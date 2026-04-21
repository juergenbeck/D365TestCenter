/**
 * D365 Test Center - Form Scripts
 *
 * Hinweis: Diese Datei ist reiner UI-Hilfscode im CRM-Formular. Die eigentliche
 * Test-Engine laeuft in C# (Plugin/Custom API/CLI), nicht im Browser.
 *
 * Exponierte Funktionen:
 *   JBE.Forms.TestRun.onLoad(executionContext)
 *   JBE.Forms.TestRunResult.onLoad(executionContext)
 *   JBE.Forms.TestStep.onLoad(executionContext)
 *   JBE.Forms.TestCase.onLoad(executionContext)
 */
"use strict";
var JBE = JBE || {};
JBE.Forms = JBE.Forms || {};

// Monospace + Pretty-Print-Autoformat fuer JSON-Memos.
// Anwendung: auf multiline Textarea-DOM-Elementen einer Form.
// Implementation: wir setzen style.fontFamily per DOM-Zugriff; zusaetzlich
// versuchen wir beim Speichern (onChange) ein JSON.parse+stringify als Pretty-Format,
// aber nur wenn der User das explizit via {"_pretty": true}-Header will -> zu invasiv,
// deshalb nur visueller Monospace.
JBE.Forms._applyMonospace = function(formContext, fieldNames) {
    try {
        fieldNames.forEach(function(n) {
            var ctrl = formContext.getControl(n);
            if (!ctrl) return;
            // ModernFlux/UCI: Zugriff via DOM-Query. Stabile Anchor-Klasse nicht garantiert,
            // daher Iteration ueber alle textareas und Abgleich auf control id.
            setTimeout(function() {
                try {
                    var root = document.querySelector('[data-id="' + n + '.fieldControl"]') ||
                               document.querySelector('[id*="' + n + '"]');
                    if (!root) return;
                    var txts = root.querySelectorAll('textarea, [contenteditable="true"]');
                    txts.forEach(function(el) {
                        el.style.fontFamily = "Consolas, 'Courier New', monospace";
                        el.style.fontSize = "12px";
                        el.style.whiteSpace = "pre";
                    });
                } catch (e) { /* best effort */ }
            }, 400);
        });
    } catch (e) { /* best effort */ }
};

// ===============================
//   Test Run
// ===============================
JBE.Forms.TestRun = JBE.Forms.TestRun || {};
JBE.Forms.TestRun._timer = null;
JBE.Forms.TestRun.onLoad = function(ctx) {
    var f = ctx.getFormContext();
    // JSON-Monospace auf fullLog + testsummary (Summary ist kein JSON, aber lange Strings sehen so besser aus)
    JBE.Forms._applyMonospace(f, ["jbe_fulllog", "jbe_testsummary"]);

    // Auto-Refresh bei laufenden Testlaeufen: alle 5 Sekunden neu laden,
    // bis der Status nicht mehr 'Laeuft' (105710001) ist oder der User navigiert.
    JBE.Forms.TestRun._startAutoRefresh(f);

    // Wenn der Status sich aendert (User klickt manuell), Timer neu bewerten.
    f.getAttribute("jbe_teststatus").addOnChange(function() {
        JBE.Forms.TestRun._startAutoRefresh(f);
    });
};

JBE.Forms.TestRun._startAutoRefresh = function(formContext) {
    if (JBE.Forms.TestRun._timer) {
        clearInterval(JBE.Forms.TestRun._timer);
        JBE.Forms.TestRun._timer = null;
    }
    var statusAttr = formContext.getAttribute("jbe_teststatus");
    if (!statusAttr) return;
    var val = statusAttr.getValue();
    // 105710001 = Running. Alle anderen Stati brauchen kein Polling.
    if (val !== 105710001) return;

    JBE.Forms.TestRun._timer = setInterval(function() {
        try {
            formContext.data.refresh(false).then(function() {
                // Nach Refresh: wenn nicht mehr running, Timer stoppen.
                var v = formContext.getAttribute("jbe_teststatus").getValue();
                if (v !== 105710001 && JBE.Forms.TestRun._timer) {
                    clearInterval(JBE.Forms.TestRun._timer);
                    JBE.Forms.TestRun._timer = null;
                }
            });
        } catch (e) { /* best effort */ }
    }, 5000);
};

// ===============================
//   Test Run Result
// ===============================
JBE.Forms.TestRunResult = JBE.Forms.TestRunResult || {};
JBE.Forms.TestRunResult.onLoad = function(ctx) {
    var f = ctx.getFormContext();
    JBE.Forms._applyMonospace(f, ["jbe_errormessage", "jbe_assertionresults", "jbe_trackedrecords"]);
};

// ===============================
//   Test Step
// ===============================
JBE.Forms.TestStep = JBE.Forms.TestStep || {};
JBE.Forms.TestStep.onLoad = function(ctx) {
    var f = ctx.getFormContext();
    JBE.Forms._applyMonospace(f, ["jbe_inputdata", "jbe_outputdata", "jbe_errormessage"]);
};

// ===============================
//   Test Case
// ===============================
JBE.Forms.TestCase = JBE.Forms.TestCase || {};
JBE.Forms.TestCase.onLoad = function(ctx) {
    var f = ctx.getFormContext();
    JBE.Forms._applyMonospace(f, ["jbe_definitionjson"]);
};
