package skua.api {

import skua.Main;

public class Shop {

    public function Shop() {
        super();
    }

    public static function buyItemByName(name:String, quantity:int = -1):void {
        var item:* = getShopItem(name);
        if (item != null) {
            if (quantity == -1)
                Main.instance.game.world.sendBuyItemRequest(item);
            else {
                var buyItem:* = {};
                buyItem.iSel = item;
                buyItem.iQty = quantity;
                buyItem.accept = 1;
                Main.instance.game.world.sendBuyItemRequestWithQuantity(buyItem);
            }
        }
    }

    public static function buyItemByID(id:int, shopItemID:int, quantity:int = -1):void {
        var item:* = getShopItemByID(id, shopItemID);
        if (item != null) {
            if (quantity == -1)
                Main.instance.game.world.sendBuyItemRequest(item);
            else {
                var buyItem:* = {};
                buyItem.iSel = item;
                buyItem.iQty = quantity;
                buyItem.accept = 1;
                Main.instance.game.world.sendBuyItemRequestWithQuantity(buyItem);
            }
        }
    }

    public static function getShopItem(name:String):* {
        var lowerName:String = name.toLowerCase();
        for each (var item:* in Main.instance.game.world.shopinfo.items) {
            if (item && item.sName.toLowerCase() == lowerName) {
                return getShopItemByID(item.ItemID, item.ShopItemID);
            }
        }
        return null;
    }

    public static function getUnmetPurchaseRequirements(itemID:int, shopItemID:int, quantity:int = -1):String {
        var item:* = getShopItemByID(itemID, shopItemID);
        if (item == null) {
            return JSON.stringify(["Item is not in the loaded shop."]);
        }
        var world:* = Main.instance.game.world;
        var missing:Array = [];
        var bundle:int = Math.max(1, int(item.iQty));
        var purchased:int = quantity == -1 ? bundle : quantity;
        if (purchased <= 0) {
            return JSON.stringify(["Purchase quantity must be positive."]);
        }
        if (item.iQSindex != null && Number(item.iQSindex) >= 0
            && Number(world.getQuestValue(item.iQSindex)) < Number(item.iQSvalue)) {
            missing.push("Quest requirement not met.");
        }
        // Match InvCat's cost per purchase bundle. Banked materials do not count.
        var bundles:int = Math.max(1, Math.floor(purchased / bundle));
        var required:Object = {};
        var names:Object = {};
        var id:int;
        if (item.turnin != null) {
            for each (var material:* in item.turnin) {
                id = int(material.ItemID);
                required[id] = (required[id] == null ? 0 : Number(required[id])) + Number(material.iQty) * bundles;
                names[id] = material.sName;
            }
        }
        if (Number(item.TokID) > 0 && Number(item.TokQty) > 0) {
            id = int(item.TokID);
            required[id] = (required[id] == null ? 0 : Number(required[id])) + int(Number(item.TokQty) * bundles);
            if (item.TokName != null && String(item.TokName).length > 0) {
                names[id] = item.TokName;
            }
        }
        for (var key:String in required) {
            var owned:* = world.invTree[int(key)];
            var have:int = owned == null ? 0 : int(owned.iQty);
            if (have < Number(required[key])) {
                var name:String = names[key] != null ? String(names[key])
                    : (owned != null ? String(owned.sName) : "Item #" + key);
                missing.push("Missing " + (Number(required[key]) - have) + " x " + name + " in inventory.");
            }
        }
        return JSON.stringify(missing);
    }

    public static function getShopItemByID(itemID:int, shopItemID:int):* {
        for each (var item:* in Main.instance.game.world.shopinfo.items) {
            if (item && item.ItemID == itemID && (shopItemID == -1 || item.ShopItemID == shopItemID)) {
                return item;
            }
        }
        return null;
    }
}
}
