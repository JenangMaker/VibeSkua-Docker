package skua.api {
import flash.display.DisplayObject;
import flash.events.Event;
import flash.events.TimerEvent;
import flash.utils.Timer;

import skua.Main;

public class World {
    // Streamer mode while on: the name and guild it shows (see streamerMode).
    private static var _streamerName:String = null;
    private static var _streamerGuild:String = null;

    private static var _fxStore:Object = {};
    private static var _fxLastOpt:Boolean = false;

    private static var _lastGoodFrame:String = "";
    private static var _lastGoodPad:String = "";

    private static const PAD_NAMES_REGEX:RegExp =
        /(Spawn|Center|Left|Right|Up|Down|Top|Bottom)/;

    public function World() {
        super();
    }

    public static function jumpCorrectRoom(
        cell:String,
        pad:String,
        autoCorrect:Boolean = true,
        clientOnly:Boolean = false
    ):void {
        var world:* = Main.instance.game.world;

        if (!autoCorrect) {
            world.moveToCell(cell, pad, clientOnly);
            return;
        }

        var users:Vector.<String> = world.areaUsers.concat();

        var myIndex:int =
            users.indexOf(Main.instance.game.sfc.myUserName);

        if (myIndex != -1)
            users.splice(myIndex, 1);

        if (users.length <= 1) {
            world.moveToCell(cell, pad, clientOnly);
        } else {
            var uoTree:* = world.uoTree;
            var usersCell:String = world.strFrame;
            var usersPad:String = "Left";

            for (var i:int = 0; i < users.length; i++) {
                var userObj:* = uoTree[users[i]];

                if (userObj == null)
                    continue;

                usersCell = userObj.strFrame;
                usersPad = userObj.strPad;

                if (cell == usersCell && pad != usersPad)
                    break;
            }

            world.moveToCell(cell, usersPad, clientOnly);
        }

        var jumpTimer:Timer = new Timer(50, 1);
        jumpTimer.addEventListener(TimerEvent.TIMER, jumpTimerEvent);
        jumpTimer.start();

        function jumpTimerEvent(e:TimerEvent):void {
            jumpCorrectPad(cell, clientOnly);

            jumpTimer.stop();
            jumpTimer.removeEventListener(
                TimerEvent.TIMER,
                jumpTimerEvent
            );
        }
    }

    public static function jumpCorrectPad(
        cell:String,
        clientOnly:Boolean = false
    ):void {
        var cellPad:String = "Left";
        var padArr:Array = getCellPads();
        var world:* = Main.instance.game.world;

        if (padArr.indexOf(cellPad) >= 0) {
            if (world.strPad == cellPad)
                return;

            world.moveToCell(cell, cellPad, clientOnly);
        } else if (padArr.length > 0) {
            cellPad = padArr[0];

            if (world.strPad == cellPad)
                return;

            world.moveToCell(cell, cellPad, clientOnly);
        }
    }

    public static function getCellPads():Array {
        var cellPads:Array = [];
        var cellPadsCnt:int =
            Main.instance.game.world.map.numChildren;

        for (var i:int = 0; i < cellPadsCnt; ++i) {
            var child:DisplayObject =
                Main.instance.game.world.map.getChildAt(i);

            if (PAD_NAMES_REGEX.test(child.name))
                cellPads.push(child.name);
        }

        return cellPads;
    }

    public static function disableDeathAd(enable:Boolean):void {
        Main.instance.game.userPreference.data.bDeathAd = !enable;
    }

    public static function skipCutscenes():void {
        var game:* = Main.instance.game;
        var world:* = game.world;
        var extSWF:* = game.mcExtSWF;

        /*
         * No cutscene is active.
         *
         * Remember the current location as the last known good
         * location and leave the player completely alone.
         */
        if (extSWF.numChildren <= 0) {
            if (world.strFrame != "") {
                _lastGoodFrame = world.strFrame;
                _lastGoodPad = world.strPad;
            }

            return;
        }

        /*
         * A cutscene is active.
         *
         * Remove the cutscene overlay.
         */
        while (extSWF.numChildren > 0)
            extSWF.removeChildAt(0);

        game.showInterface();

        /*
         * Return to the last known good location.
         *
         * If we haven't recorded one yet, fall back to Enter/Spawn.
         */
        var targetFrame:String = _lastGoodFrame != ""
            ? _lastGoodFrame
            : "Enter";

        var targetPad:String = _lastGoodPad != ""
            ? _lastGoodPad
            : "Spawn";

        /*
         * Let AQW finish removing the cutscene before jumping.
         */
        var jumpTimer:Timer = new Timer(50, 1);
        jumpTimer.addEventListener(TimerEvent.TIMER, performJump);
        jumpTimer.start();

        function performJump(e:TimerEvent):void {
            jumpTimer.stop();
            jumpTimer.removeEventListener(
                TimerEvent.TIMER,
                performJump
            );

            jumpCorrectRoom(
                targetFrame,
                targetPad,
                true,
                false
            );

            /*
             * Allow the room/pad correction to finish before
             * refreshing the display.
             */
            var refreshTimer:Timer = new Timer(250, 1);
            refreshTimer.addEventListener(
                TimerEvent.TIMER,
                performRefresh
            );
            refreshTimer.start();

            function performRefresh(e:TimerEvent):void {
                refreshTimer.stop();
                refreshTimer.removeEventListener(
                    TimerEvent.TIMER,
                    performRefresh
                );

                refreshWorld();
            }
        }
    }

    private static function refreshWorld():void {
        /*
         * AQW can leave the world display black after a cutscene.
         *
         * Do the same visibility transition as toggling Lag Killer,
         * which forces Flash to redraw the world.
         */
        killLag(true);

        var refreshTimer:Timer = new Timer(100, 1);
        refreshTimer.addEventListener(
            TimerEvent.TIMER,
            restoreWorld
        );
        refreshTimer.start();

        function restoreWorld(e:TimerEvent):void {
            refreshTimer.stop();
            refreshTimer.removeEventListener(
                TimerEvent.TIMER,
                restoreWorld
            );

            killLag(false);
        }
    }

    public static function hidePlayers(enabled:Boolean):void {
        var world:* = Main.instance.game.world;
        var currentFrame:String = world.strFrame;

        for each (var avatar:* in world.avatars) {
            if (avatar != null &&
                avatar.pnm != null &&
                !avatar.isMyAvatar) {

                if (enabled) {
                    avatar.hideMC();
                } else if (avatar.strFrame == currentFrame) {
                    avatar.showMC();
                }
            }
        }
    }

    public static function disableFX(enabled:Boolean):void {
        if (!_fxLastOpt && enabled)
            _fxStore = {};

        _fxLastOpt = enabled;

        for each (var avatar:* in Main.instance.game.world.avatars) {
            if (enabled) {
                if (avatar.pMC.spFX != null)
                    _fxStore[avatar.uid] = avatar.rootClass.spFX;

                avatar.rootClass.spFX = null;
            } else {
                avatar.rootClass.spFX = _fxStore[avatar.uid];
            }
        }
    }

    public static function killLag(enable:Boolean):void {
        Main.instance.game.world.visible = !enable;

        if (Main.instance.customBGLagKiller)
            Main.instance.customBGLagKiller.visible = enable;
    }

    /**
     * The script timer's options in one call: it made up to six calls four
     * times a second (lag killer, magnetize, infinite range, skip cutscenes
     * and the two Lite untarget settings, read then set). Each part fails on
     * its own, as the separate calls did.
     */
    public static function keepOptions(lagKiller:Boolean, magnetize:Boolean, infiniteRange:Boolean, skipCutscenes:Boolean, untarget:Boolean):void {
        if (lagKiller) try { killLag(true); } catch (e:*) {}
        if (magnetize) try { Combat.magnetize(); } catch (e:*) {}
        if (infiniteRange) try { Combat.infiniteRange(); } catch (e:*) {}
        if (skipCutscenes) try { World.skipCutscenes(); } catch (e:*) {}
        if (untarget) try {
            var pref:* = Main.instance.game.litePreference.data;
            if (!pref.bUntargetSelf) pref.bUntargetSelf = true;
            if (!pref.bUntargetDead) pref.bUntargetDead = true;
        } catch (e:*) {}
    }

    /**
     * Streamer mode on, with the name and guild to show. From then on the
     * masking runs on every frame just before it is drawn (Event.RENDER,
     * asked for each frame with stage.invalidate()), after the frame's
     * scripts and server messages: the game writes the real name and the
     * "map-room" title back when it changes map or room, and Skua's
     * half-second pass from outside left them on screen until it came (the
     * room number for 216-513 ms after a map change). Skua still calls this
     * now and then, which arms it again after the game is reloaded.
     */
    public static function streamerMode(name:String, guild:String):void {
        var armed:Boolean = _streamerName != null;
        _streamerName = name;
        _streamerGuild = guild;
        if (!armed) {
            Main.instance.addEventListener(Event.ENTER_FRAME, streamerFrame);
            Main.instance.addEventListener(Event.RENDER, streamerRender);
        }
        maskStreamer();
    }

    /** Streamer mode off: no more masking (Skua puts the real labels back). */
    public static function streamerModeOff():void {
        _streamerName = _streamerGuild = null;
        Main.instance.removeEventListener(Event.ENTER_FRAME, streamerFrame);
        Main.instance.removeEventListener(Event.RENDER, streamerRender);
    }

    private static function streamerFrame(e:Event):void {
        if (Main.instance.stage != null)
            Main.instance.stage.invalidate();
    }

    private static function streamerRender(e:Event):void {
        if (_streamerName != null)
            maskStreamer();
    }

    /**
     * The name tag, guild and portraits say the streamer name/guild, the real
     * name labels and the chat log stay hidden, and the area title shows the
     * bare map name. Each part fails on its own, and only writes what differs:
     * setting a text field's text lays it out again even when it is the same,
     * and this runs every frame.
     */
    private static function maskStreamer():void {
        var game:* = Main.instance.game;
        try { setText(game.world.myAvatar.pMC.pname.ti, _streamerName); } catch (e:*) {}
        try { setText(game.world.myAvatar.pMC.pname.tg, _streamerGuild); } catch (e:*) {}
        try { setText(game.world.rootClass.ui.mcPortrait.strName, _streamerName); } catch (e:*) {}
        try { setText(game.world.rootClass.ui.mcPortraitTarget.strName, _streamerGuild); } catch (e:*) {}
        try { hide(game.world.myAvatar.pMC.pname.ti); } catch (e:*) {}
        try { hide(game.world.rootClass.ui.mcPortrait.strName); } catch (e:*) {}
        try { hide(game.world.rootClass.ui.mcInterface.t1); } catch (e:*) {}
        try {
            var map:* = game.world.strMapName;
            if (map is String && map.length > 0) {
                setText(game.ui.mcInterface.areaList.title.t1, map.charAt(0).toUpperCase() + map.substr(1).toLowerCase());
                // A house map titles itself "<owner>'s House".
                if (map.toLowerCase() == "house")
                    setText(game.world.map.txtHouse, "House");
            }
        } catch (e:*) {}
    }

    private static function setText(field:*, text:String):void {
        if (field != null && field.text != text)
            field.text = text;
    }

    private static function hide(item:*):void {
        if (item != null && item.visible)
            item.visible = false;
    }
}
}