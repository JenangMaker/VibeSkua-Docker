package skua.api {
import flash.display.MovieClip;
import flash.events.Event;
import flash.events.MouseEvent;
import flash.events.TimerEvent;
import flash.utils.Timer;

import skua.Main;
import skua.module.ModalMC;

public class Server {

    public function Server() {
        super();
    }

    public static function clickServer(serverName:String):String {
        var source:* = Main.instance.game.mcLogin.sl.iList;
        for (var i:int = 0; i < source.numChildren; i++) {
            var child:* = source.getChildAt(i);

            if (child.tName.ti.text.toLowerCase().indexOf(serverName.toLowerCase()) > -1) {
                child.dispatchEvent(new MouseEvent(MouseEvent.CLICK));
                return true.toString();
            }
        }
        return false.toString();
    }

    public static function connectToServer(server:String):String {
        var serverData:Object = JSON.parse(server);
        var objLogin:Object = null;

        var connectionServerTimer:Timer = new Timer(500, 50);
        connectionServerTimer.addEventListener(TimerEvent.TIMER, connectingServer);
        connectionServerTimer.start();

        function connectingServer(e:Event):void {
            if (objLogin != null) {
                connectServer(serverData, objLogin);
                connectionServerTimer.stop();
                connectionServerTimer.removeEventListener(TimerEvent.TIMER, connectingServer);
            }
            objLogin = JSON.parse(Main.getGameObjectS("objLogin"));
        }

        return true.toString();
    }

    private static function connectServer(server:Object, objLoginData:Object):* {
        Main.instance.game.showTracking("4");
        if (Main.instance.game.serialCmdMode) {
            return;
        }

        if (server.bOnline == 0) {
            Main.instance.game.MsgBox.notify("Server currently offline!");
        } else if (server.iCount >= server.iMax) {
            Main.instance.game.MsgBox.notify("Server is Full!");
        } else if (server.iChat > 0 && objLoginData.bCCOnly == 1) {
            Main.instance.game.MsgBox.notify("Account Restricted to Moglin Sage Server Only.");
        } else if (server.iChat > 0 && objLoginData.iAge < 13 && objLoginData.iUpgDays < 0) {
            Main.instance.game.MsgBox.notify("Ask your parent to upgrade your account in order to play on chat enabled servers.");
        } else if (server.bUpg == 1 && objLoginData.iUpgDays < 0) {
            showModal("Member Server! Do you want to upgrade your account to access this premium server now?", "white,medium", "dual");
        } else if (Number(server.iMax) % 2 > 0) {
            showModal("Testing Server! Do you want to switch to the testing game client?", "white,medium", "dual");
        } else if (server.iLevel > 0 && objLoginData.iEmailStatus <= 2) {
            showModal("This server requires a confirmed email address.", "red,medium", "mono");
        } else {
            Main.instance.game.objServerInfo = server;
            Main.instance.game.chatF.iChat = server.iChat;
            killLoginModals();
            Main.instance.game.connectTo(server.sIP, server.iPort);
        }
    }

    private static function showModal(message:String, glow:String, buttons:String):void {
        var modal:ModalMC = new ModalMC();
        var params:Object = {
            strBody: message,
            params: {},
            glow: glow,
            btns: buttons
        };
        Main.instance.game.mcLogin.ModalStack.addChild(modal);
        modal.init(params);
    }

    // Before a relogin connects: every dialog and popup that could block it.
    public static function killLoginModals():void {
        try {
            if (Main.instance.game == null) return;
            var game:* = Main.instance.game;
            try {
                if (game.mcLogin != null && game.mcLogin.ModalStack != null)
                    cleanModalStack(game.mcLogin.ModalStack);
            } catch (e:Error) {}
            // mcO is a sealed mcOption without ModalStack: test "in" first (#1069).
            try {
                if (game.mcO != null) {
                    if ("ModalStack" in game.mcO && game.mcO.ModalStack != null)
                        cleanModalStack(game.mcO.ModalStack);
                    try {
                        if (game.mcO.cnt != null && game.mcO.cnt.hit != null)
                            game.mcO.cnt.hit.visible = false;
                    } catch (e:Error) {}
                }
            } catch (e:Error) {}
            try {
                if (game.ui != null && game.ui.ModalStack != null)
                    cleanModalStack(game.ui.ModalStack);
            } catch (e:Error) {}
            try {
                if (game.MsgBox != null && "fClose" in game.MsgBox)
                    game.MsgBox.fClose();
            } catch (e:Error) {}
            // The kick / ban warning frame.
            try {
                if (game.mcLogin != null && game.mcLogin.warning != null)
                    game.mcLogin.warning.visible = false;
            } catch (e:Error) {}
            try {
                if (game.ui != null && game.ui.mcPopup != null)
                    cleanModalStack(game.ui.mcPopup);
            } catch (e:Error) {}
            try {
                if ("serialCmdMode" in game) game.serialCmdMode = false;
                if ("connectingServer" in game) game.connectingServer = false;
            } catch (e:Error) {}
        } catch (e:Error) {
            Main.instance.external.debug("Error in killLoginModals: " + e);
        }
    }

    private static function cleanModalStack(stack:*):void {
        if (stack == null) return;
        try {
            for (var i:int = stack.numChildren - 1; i >= 0; i--) {
                var child:* = stack.getChildAt(i);
                if (child == null) continue;
                try {
                    if ("fClose" in child && typeof(child.fClose) == "function")
                        child.fClose();
                } catch (e:Error) {}
                try {
                    if (stack.contains(child))
                        stack.removeChild(child);
                } catch (e:Error) {}
            }
        } catch (e:Error) {}
    }

    // The text of the dialog the login is stuck on ("" when there is none).
    public static function getActiveModalMessage():String {
        try {
            var game:* = Main.instance.game;
            if (game == null) return "";
            if (game.mcO != null && "ModalStack" in game.mcO && game.mcO.ModalStack != null) {
                var text:String = modalText(game.mcO.ModalStack);
                if (text != null) return text;
            }
            if (game.mcLogin != null && game.mcLogin.ModalStack != null) {
                var lText:String = modalText(game.mcLogin.ModalStack);
                if (lText != null) return lText;
            }
            if (game.MsgBox != null && game.MsgBox.stage != null && game.MsgBox.cnt != null && game.MsgBox.cnt.strBody != null)
                return String(game.MsgBox.cnt.strBody.text);
            if (game.mcLogin != null && game.mcLogin.warning != null && game.mcLogin.warning.visible
                    && game.mcLogin.warning.strMessage != null && game.mcLogin.warning.strMessage.text != null)
                return String(game.mcLogin.warning.strMessage.text);
        } catch (e:Error) {}
        return "";
    }

    private static function modalText(stack:*):String {
        if (stack.numChildren == 0) return null;
        var m:* = stack.getChildAt(0);
        if (m == null) return null;
        if (m.cnt != null && m.cnt.strBody != null && m.cnt.strBody.text != null) return String(m.cnt.strBody.text);
        if (m.strBody != null) return String(m.strBody);
        return null;
    }
}
}
