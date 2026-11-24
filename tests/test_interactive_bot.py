import socket
import struct
import time
import threading
import sys
import os
import datetime

# PacketType enum matching OurSummerCar
PACKET_PING = 1
PACKET_PONG = 2
PACKET_PLAYER_SYNC = 10
PACKET_DOOR_TOGGLE = 20
PACKET_GARAGE_SYNC = 22
PACKET_ITEM_SYNC = 32
PACKET_JERRY_CAN_LID = 40
PACKET_JERRY_CAN_FLUID = 41
PACKET_TOOL_SET_TOGGLE = 42
PACKET_LIGHT_SWITCH_TOGGLE = 50
PACKET_SAUNA_KNOB = 52
PACKET_SAUNA_STEAM = 54
PACKET_VEHICLE_DOOR_TOGGLE = 60
PACKET_VEHICLE_DOOR_ROTATION = 61

LOG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "bot_log.txt")

def log(msg):
    now_str = datetime.datetime.now().strftime("%H:%M:%S")
    formatted = f"[{now_str}] {msg}"
    print(formatted)
    try:
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(formatted + "\n")
    except Exception:
        pass

def send_packet(sock, ptype, payload=b''):
    data = bytes([ptype]) + payload
    framed = struct.pack('<I', len(data)) + data
    try:
        sock.sendall(framed)
    except Exception as e:
        log(f"[!] Ошибка отправки пакета: {e}")

def listen_loop(sock, stop_event):
    while not stop_event.is_set():
        try:
            raw_len = sock.recv(4)
            if not raw_len or len(raw_len) < 4:
                break
            length = struct.unpack('<I', raw_len)[0]
            data = b''
            while len(data) < length:
                chunk = sock.recv(length - len(data))
                if not chunk:
                    break
                data += chunk

            if len(data) == 0:
                continue

            ptype = data[0]

            if ptype == PACKET_PING:
                send_packet(sock, PACKET_PONG, data[1:])

            elif ptype == PACKET_JERRY_CAN_LID:
                is_diesel = bool(data[1])
                is_open = bool(data[2])
                name = "Дизель" if is_diesel else "Бензин"
                state = "ОТКРЫТА" if is_open else "ЗАКРЫТА"
                log(f">>> [СЕТЬ] Канистра ({name}): крышка -> {state}")

            elif ptype == PACKET_JERRY_CAN_FLUID:
                gas, diesel = struct.unpack('<ff', data[1:9])
                log(f">>> [СЕТЬ] Уровень топлива: Бензин = {gas:.1f}л, Дизель = {diesel:.1f}л")

            elif ptype == PACKET_TOOL_SET_TOGGLE:
                is_ratchet = bool(data[1])
                is_open = bool(data[2])
                name = "Трещотка" if is_ratchet else "Набор ключей (Spanner)"
                state = "ОТКРЫТ" if is_open else "ЗАКРЫТ"
                log(f">>> [СЕТЬ] Инструменты ({name}): ящик -> {state}")

            elif ptype == PACKET_LIGHT_SWITCH_TOGGLE:
                switch_hash, state = struct.unpack('<ib', data[1:6])
                st_str = "ВКЛ" if state else "ВЫКЛ"
                log(f">>> [СЕТЬ] Выключатель света #{switch_hash:X} -> {st_str}")

            elif ptype == PACKET_VEHICLE_DOOR_TOGGLE:
                door_hash, is_open = struct.unpack('<ib', data[1:6])
                st_str = "ОТКРЫТА" if is_open else "ЗАКРЫТА"
                log(f">>> [СЕТЬ] Дверь/капот авто #{door_hash:X} -> {st_str}")

            elif ptype == PACKET_SAUNA_KNOB:
                is_timer = bool(data[1])
                val = struct.unpack('<f', data[2:6])[0]
                knob_name = "Таймер" if is_timer else "Термостат"
                log(f">>> [СЕТЬ] Сауна: ручка {knob_name} -> {val:.1f}")

            elif ptype == PACKET_SAUNA_STEAM:
                log(f">>> [СЕТЬ] Сауна: ПАР НА КАМЕНКЕ! (Вода вылита)")

        except Exception as e:
            log(f"[!] Соединение разорвано: {e}")
            break

def puppet_sync(sock, stop_event):
    angle = 0.0
    while not stop_event.is_set():
        time.sleep(0.05)
        angle += 0.04
        bx = -9.0 + 1.0 * (angle % 2.0)
        by = -0.35
        bz = 8.5
        payload = bytes([PACKET_PLAYER_SYNC]) + struct.pack('<Ifffffb', 2, bx, by, bz, 180.0, 0.0, 1)
        send_packet(sock, PACKET_PLAYER_SYNC, payload)

def run_bot():
    host = '127.0.0.1'
    port = 7777

    # Очищаем лог-файл
    with open(LOG_FILE, "w", encoding="utf-8") as f:
        f.write(f"=== Бот-тестер запущен {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')} ===\n")

    log("[*] Бот ожидает запуск игры и открытие хоста на 127.0.0.1:7777...")

    while True:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.connect((host, port))
            log("[+] Бот успешно подключился к серверу как Игрок #2!")

            # Отправка тестового пинга
            ticks = int(time.time() * 10000000)
            send_packet(s, PACKET_PING, struct.pack('<q', ticks))

            stop_event = threading.Event()
            listener = threading.Thread(target=listen_loop, args=(s, stop_event), daemon=True)
            listener.start()

            puppet = threading.Thread(target=puppet_sync, args=(s, stop_event), daemon=True)
            puppet.start()

            log("[+] Бот стоит во дворе гаража и слушает все ваши действия.")
            log("[+] Вся входящая активность записывается в bot_log.txt")

            # Бот каждые 30 секунд шлет тестовый пинг для поддержания канала
            while listener.is_alive():
                time.sleep(10)
                ticks = int(time.time() * 10000000)
                send_packet(s, PACKET_PING, struct.pack('<q', ticks))

            stop_event.set()
            s.close()
            log("[-] Отключено от хоста. Повторная попытка через 3 сек...")
            time.sleep(3)

        except (ConnectionRefusedError, socket.error):
            time.sleep(2)
        except Exception as e:
            log(f"[!] Непредвиденная ошибка: {e}")
            time.sleep(3)

if __name__ == '__main__':
    run_bot()
