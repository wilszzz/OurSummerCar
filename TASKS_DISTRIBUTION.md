# Распределение задач по чатам разработки OurSummerCar

Файл сформирован на основе детального анализа кодовой базы и баг-репортов. Задачи распределены по специализированным чатам разработки в соответствии со структурой проекта:

---

## 1. Чат: «Синхронизация анимаций»

### Задача: Восстановление бутылки пива в руке во время питья (`drinkbeer`)
* **Связанные файлы:**
  * `Player/RemotePuppet.cs` (строки 190–195, 870–944)
  * `Player/PuppetAnimationController.cs` (строки 505–523)
  * `Items/ItemManager.cs` (строка 2130)

#### Суть проблемы и первопричина:
1. **В префабе куклы уже есть нативная модель бутылки:**
   В бандле `clothes.unity3d` внутри скелета куклы `hand_left` уже существует дочерний объект `beer_bottle` (как в WreckMP: `hand_left/beer_bottle`).
2. **Ошибочный спавн в `RemotePuppet.cs`:**
   Вместо того чтобы взять существующий объект `handLeftBone.Find("beer_bottle")`, метод `GetOrCreateBeerBottle()` ищет сцену на совпадение имен `"BeerBottle"`, `"beer bottle(itemx)"`, `"beer(itemx)"`. В MSC бутылки при извлечении из ящика называются `bottle(Clone)`. Поиск возвращает `null`, и код создает временный примитив `PrimitiveType.Cylinder` с процедурным материалом, который оказывается перевернутым, со смещенным скейлом или удаляется.
3. **Блокировка бутылок в руках через `ItemManager.cs`:**
   В строке 2130 `IsValidPortableItem` явно исключает бутылки:
   ```csharp
   if ((nameLower.Contains("bottle") || nameLower.Contains("empty")) && !nameLower.Contains("case")) return false;
   ```
   Из-за этого удерживаемая игроком бутылка пива не попадает в `heldItemsByPlayer` и не привязывается к руке второго игрока.

#### ТЗ для чата «Синхронизация анимаций»:
1. В `Player/RemotePuppet.cs` переписать получение `beerBottleObject`:
   - Сначала искать нативную бутылку в модели: `var nativeBottle = handLeftBone.Find("beer_bottle")?.gameObject;`
   - Если найдена — активировать ее компоненты и выставить `beerBottleObject = nativeBottle;`.
   - В `GetOrCreateBeerBottle` добавить поиск по `bottle(Clone)` и `bottle` на случай отсутствия в префабе.
2. В `Player/PuppetAnimationController.cs`:
   - Убедиться, что при вызове жеста 7 (`drinkbeer`) `beerBottleObject.SetActive(true)` активирует видимость на всё время анимации глотка (2.4–3.3 сек) и деактивирует после отрыжки (`burb`).
3. В `Items/ItemManager.cs`:
   - Разрешить для держания в руках полные бутылки пива (`nameLower.Contains("bottle") && !nameLower.Contains("empty")`), чтобы доставаемая из ящика бутылка висела в правой руке куклы до момента распития.

---

## 2. Чат: «Синхронизация квестов»

### Задача: Синхронизация доступа к авто (ключи фургона Hayosiko и Gifu от дяди Кессели)
* **Связанные файлы:**
  * `Jobs/StorylinesAndQuestsManager.cs` (строки 268–291, 442–454, 675–706)
  * `Network/PacketType.cs`

#### Суть проблемы и первопричина:
1. **Как устроен доступ к авто в MSC:**
   В My Summer Car проверка наличия ключей и возможность повернуть замок зажигания / сесть за руль Hayosiko, Gifu, Ruscko, Ferndale завязана на глобальные переменные PlayMaker:
   * `PlayMakerGlobals / FsmVariables.GlobalVariables.FindFsmBool("PlayerKeyHayosiko")`
   * `PlayMakerGlobals / FsmVariables.GlobalVariables.FindFsmBool("PlayerKeyGifu")`
   * `PlayerKeyRuscko`, `PlayerKeyFerndale`
2. **Что происходит сейчас:**
   Когда дядя отдает ключи на кухне/у дома, FSM дяди переключает состояние и выдает ключ **только локальному игроку** (хосту).
   В `StorylinesAndQuestsManager.cs` метод `HandleUncleSync` обновляет стейт `uncleStatesFsm` (`UncleStage`, `GifuKeys`), но **не выставляет глобальные булевые переменные ключей `PlayerKeyHayosiko` и `PlayerKeyGifu`** у подключенного клиента!
3. **Отсутствие снимка ключей при входе (Late-Join):**
   Если клиент подключился после того, как дядя отдал ключи хосту, клиент вообще не знает о полученных правах на вождение фургона.

#### ТЗ для чата «Синхронизация квестов»:
1. В `StorylinesAndQuestsManager.cs`:
   - При обработке пакета дяди `HandleUncleSync`:
     * Если `hasVan == true` или `stage >= 2`: выставлять глобальную переменную `PlayMakerGlobals`:
       ```csharp
       var vanKey = FsmVariables.GlobalVariables.FindFsmBool("PlayerKeyHayosiko");
       if (vanKey != null) vanKey.Value = true;
       ```
     * Если `hasGifu == true`:
       ```csharp
       var gifuKey = FsmVariables.GlobalVariables.FindFsmBool("PlayerKeyGifu");
       if (gifuKey != null) gifuKey.Value = true;
       ```
2. Создать пакет синхронизации всех ключей авто (`PlayerKeysSnapshot` / `PacketType.PlayerKeysSync`):
   - При подключении клиента хост отправляет текущие статусы:
     `PlayerKeyHayosiko`, `PlayerKeyGifu`, `PlayerKeyRuscko`, `PlayerKeyFerndale`.
   - Клиент при получении пакета синхронизирует свои глобальные `FsmVariables.GlobalVariables`.

---

## 3. Чат: «Мультиплеерные фичи»

### Задача 1: Плавное открытие гаражных ворот без рывков (`DoorManager.cs`)
* **Связанный файл:** `Doors/DoorManager.cs` (строки 305–345, 376–458)

#### Суть проблемы и первопричина:
1. **Сброс локального движения при отпускании кнопки:**
   В хуках гаража повешен обработчик на стейт FSM `"Mouse off"`:
   ```csharp
   InsertAction(garageLeftFsm, "Mouse off", () => { lastLocalMoveTimeLeft = 0f; }, 0);
   ```
   Когда игрок просто «толкнул» дверь и отпустил мышь, `lastLocalMoveTimeLeft` сбрасывается в 0. Условие `isLeftLocal` мгновенно становится `false`. Пакеты перестают отправляться второму игроку, хотя створка физически продолжает открываться/закрываться по инерции.
2. **Обнуление скоростей Rigidbody:**
   В `ApplyGarageAngle`:
   ```csharp
   rb.velocity = Vector3.zero;
   rb.angularVelocity = Vector3.zero;
   ```
   Каждый сетевой тик насильно гасит скорость створки, разрушая плавность.
3. **Резкие скачки при возобновлении:**
   В `HandleGarageDoorSync` при паузе между пакетами > 1.5 сек срабатывает мгновенный жесткий телепорт: `ApplyGarageAngle(...)`, вместо плавной интерполяции.

#### ТЗ для чата «Мультиплеерные фичи»:
1. В `DoorManager.cs`:
   - Убрать обнуление `lastLocalMoveTimeLeft = 0f;` в стейте `"Mouse off"`. Створка считается локально движущейся, пока угловая скорость вращения створки `Math.Abs(Mathf.DeltaAngle(curLeft, lastSentLeftAngle)) > 0.3f`.
   - Не глушить `angularVelocity` в `ApplyGarageAngle`, если створка находится в плавном сетевом движении.
   - В `InterpolateRemoteDoors` использовать динамическую скорость `SmoothDampAngle` с плавным переходом до полной остановки створки.

---

### Задача 2: Синхронизация перемещения крана двигателя (`motor hoist`) и домкрата (`floor jack`)
* **Связанные файлы:**
  * `Equipment/EquipmentManager.cs`
  * `Network/PacketType.cs`

#### Суть проблемы и первопричина:
1. Сейчас `EquipmentManager.cs` синхронизирует **только** угол стрелы крана (`motorHoistAngle`) и высоту подъема домкрата (`floorJackY`).
2. Координаты и поворот самих объектов `motor hoist(itemx)` и `floor jack(itemx)` в мире **не передаются**:
   - В `ItemManager.cs` они не отслеживаются из-за порога массы `rb.mass > 160f` и того, что их не берут в руки через `PickUp`, а толкают ногами или тащат по полу гаража.
   - В `EquipmentManager.cs` нет трекера позиции/поворота базовых `Transform` домкрата и крана.

#### ТЗ для чата «Мультиплеерные фичи»:
1. В `Equipment/EquipmentManager.cs`:
   - Кэшировать ссылки на `Transform` и `Rigidbody` самих объектов крана и домкрата:
     `Transform jackTr`, `Transform hoistTr`.
   - В цикле `Update()` проверять изменение их позиции (`Vector3.Distance > 0.05f`) или поворота (`Quaternion.Angle > 2f`).
   - Добавить отправку пакета `HeavyEquipmentTransformSync` (ID оборудования: 0=Jack, 1=Hoist, Position, Rotation) при их физическом перемещении.
   - На принимающей стороне плавно интерполировать `position` и `rotation` крана/домкрата через `Vector3.Lerp` / `Quaternion.Slerp`.

---

### Задача 3: Исправление дёрганья дверей транспорта (`VehicleDoorManager.cs`)
* **Связанный файл:** `Vehicles/VehicleDoorManager.cs` (строки 93–100, 324–393)

#### Суть проблемы и первопричина:
1. **Взаимный пинг-понг пакетов без владельца:**
   В методе `Update()` таймер `angleSyncTimer` (10 раз в сек) вызывает `SyncOpenDoorAngles()`.
   Если дверь открыта (`d.DoorOpenVar.Value == true`), **оба игрока одновременно** начинают спамить пакетами `VehicleDoorRotation` с локальными углами.
2. **Мгновенный Snap угла без сглаживания:**
   При получении `VehicleDoorRotation`:
   ```csharp
   door.DoorObject.transform.localEulerAngles = angles;
   ```
   Локальная плавная анимация открывания двери от PlayMaker FSM перебивается сетевыми значениями из прошлого кадра, вызывая бесконечную тряску и вибрацию у обоих игроков.
3. **Особый случай сдвижной двери фургона (Hayosiko Side Door):**
   Сдвижная дверь фургона перемещается по направляющим (позиция), а код пытается вращать ее вокруг оси Y как обычную распашную дверь.

#### ТЗ для чата «Мультиплеерные фичи»:
1. В `VehicleDoorManager.cs`:
   - Ввести понятие локального инициатора (кто кликнул на ручку двери): отправлять углы двери должен **только тот игрок, который открывает/тянет дверь в данный момент**.
   - Второй игрок должен просто воспроизводить открывание через вызов нативного события FSM `MP_OPEN` / `MP_CLOSE`, позволяя нативной плавности MSC выполнить анимацию.
   - Исключить сдвижную дверь `IsHayosikoSidedoor` из `SyncOpenDoorAngles` (она должна управляться только триггерами FSM открытия/закрытия).
   - Если промежуточный угол всё же передается, применять его через `Mathf.LerpAngle` / `SmoothDampAngle`, а не жестким присваиванием `localEulerAngles`.

---

## 4. Общий справочник переменных и объектов MSC (для справок)

### Справочные данные и переменные для разработчиков:
* **Ключи авто в PlayMakerGlobals:**
  * `PlayerKeyHayosiko` (FsmBool) — ключ от фургона дяди.
  * `PlayerKeyGifu` (FsmBool) — ключ от ассенизатора Gifu.
  * `PlayerKeyRuscko` (FsmBool) — ключ от Русько.
  * `PlayerKeyFerndale` (FsmBool) — ключ от маслкара Флитари.
* **Нативная модель бутылки куклы:**
  * Путь в иерархии префаба `char.prefab` (`clothes.unity3d`):
    `char/skeleton/pelvis/RotationBendPivot/spine_middle/spine_upper/collar_left/shoulder_left/arm_left/hand_left/beer_bottle`
* **Тяжелое оборудование гаража:**
  * Домкрат: `floor jack(itemx)`
  * Кран двигателя: `motor hoist(itemx)`
* **FSM сдвижной двери фургона:**
  * `HAYOSIKO(1500kg, 250)/SideDoor/door/Collider` -> FSM `Use` (переходы `Open door` / `Close door`).
