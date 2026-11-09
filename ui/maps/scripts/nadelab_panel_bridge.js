// Official cs_script API owns construction of CustomHUD network state. CSS
// 1.0.376's Windows string setter cannot resolve this build's struct RTTI.
import { Instance } from "cs_script/point_script";

let inbox;
let connection;
let activeLayout;
Instance.OnScriptInput("layout", ({ caller }) => {
    if (caller?.IsValid() && caller.GetClassName() === "custom_hud_layout") activeLayout = caller;
});
Instance.OnScriptInput("attach", ({ caller }) => {
    if (!caller?.IsValid() || caller.GetClassName() !== "logic_case") return;
    if (inbox === caller && connection !== undefined) { caller.SetHealth(1); return; }
    if (connection !== undefined) Instance.DisconnectOutput(connection);
    inbox = caller;
    connection = Instance.ConnectOutput(inbox, "OnDefault", ({ value, activator: player }) => {
        try {
            const layout = activeLayout;
            // Allow JSON quote/backslash escaping for the independently bounded
            // body (480 bytes) and shortcuts (360 bytes); field limits stay below.
            if (typeof value !== "string" || value.length > 2048 || !inbox?.IsValid() ||
                !layout?.IsValid() || layout.GetClassName() !== "custom_hud_layout" ||
                !player?.IsValid() || player.GetClassName() !== "cs_player_controller") return;
            const message = JSON.parse(value);
            // Older senders omit keys; clearing that field prevents a stale
            // footer from surviving a plain-text menu update or a hide.
            const keys = message.keys === undefined ? "" : message.keys;
            if (!Number.isInteger(message.seq) || message.seq < 2 || message.seq > 1000000000 ||
                typeof message.text !== "string" || message.text.length > (message.channel === "dummies" ? 900 : 480) ||
                typeof message.show !== "boolean" || !["main", "notice", "dummies", "cue"].includes(message.channel) ||
                typeof keys !== "string" || keys.length > 360 || keys.split("\n").length > 2 ||
                (message.channel !== "main" && keys.length !== 0)) return;
            const slot = player.GetPlayerSlot();
            layout.SetInputCaptureEnabled(slot, false);
            const notice = message.channel === "notice";
            const dummies = message.channel === "dummies";
            const cue = message.channel === "cue";
            layout.SetDialogVariableStringForPlayer(slot, cue ? "NadeLabCueText" : dummies ? "NadeLabDummiesText" : notice ? "NadeLabNoticeText" : "NadeLabText", "text", message.show ? message.text : "");
            if (!notice && !dummies && !cue) {
                // Body and shortcuts share one message, owner, sequence and
                // parent window so retries/hides cannot leave an old footer.
                layout.SetDialogVariableStringForPlayer(slot, "NadeLabKeys", "text", message.show ? keys : "");
                layout.SetHasClassForPlayer(slot, "NadeLabKeys", "nl-visible", message.show && keys.length !== 0);
            }
            layout.SetHasClassForPlayer(slot, cue ? "NadeLabCue" : dummies ? "NadeLabDummies" : notice ? "NadeLabNotice" : "NadeLabWindow", "nl-visible", message.show);
            inbox.SetHealth(message.seq);
            // Outputs run after AcceptInput returns; acknowledge each update.
            // UI text is never concatenated into a console command.
            Instance.ServerCommand("css_nl_panel_ack " + message.seq);
        } catch (error) {
            if (inbox?.IsValid()) inbox.SetHealth(-1);
            Instance.Msg("NadeLab panel bridge: " + String(error));
        }
    });
    caller.SetHealth(connection !== undefined ? 1 : -1);
});
