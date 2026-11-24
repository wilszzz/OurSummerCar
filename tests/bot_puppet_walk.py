import socket
import struct
import time
import math

def run_bot(host='127.0.0.1', port=7777, duration=120):
    print(f"[*] Подключение бота к {host}:{port}...")
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.connect((host, port))
        s.setblocking(False)
        print("[+] Бот подключен как Игрок #2!")
    except Exception as e:
        print(f"[-] Ошибка подключения: {e}")
        return

    # Отправляем Ping
    ticks = int(time.time() * 10000000)
    s.sendall(struct.pack('<I', 9) + bytes([1]) + struct.pack('<q', ticks))

    start_time = time.time()
    last_send = 0
    angle = 0.0

    # Координаты двора возле дома
    center_x = -8.2
    center_y = -0.35  # Уровень земли во дворе
    center_z = 8.1
    found_host = False

    print("[*] Бот на земле во дворе. Ожидаем пакеты...")

    try:
        while time.time() - start_time < duration:
            try:
                raw_len = s.recv(4)
                if len(raw_len) == 4:
                    length = struct.unpack('<I', raw_len)[0]
                    data = s.recv(length)
                    if len(data) >= 26 and data[0] == 10: # PlayerSync
                        pid, px, py, pz, rotY, pitch, anim = struct.unpack('<Ifffffb', data[1:26])
                        if not found_host:
                            found_host = True
                            # Ставим бота в 2.5 метрах перед игроком, но строго на уровне земли
                            center_x = px + 2.5 * math.sin(math.radians(rotY))
                            center_z = pz + 2.5 * math.cos(math.radians(rotY))
                            center_y = py - 4.2 if py > 2.0 else py
                            print(f"[+] Хост на ({px:.1f}, {py:.1f}, {pz:.1f}). Бот опущен на землю: y = {center_y:.2f}")
            except BlockingIOError:
                pass
            except Exception as e:
                print(f"[!] Ошибка сокета: {e}")
                break

            now = time.time()
            if now - last_send >= 0.05: # 20 Hz
                last_send = now
                angle += 0.06
                if angle > 2 * math.pi:
                    angle -= 2 * math.pi

                radius = 1.8
                curr_x = center_x + radius * math.cos(angle)
                curr_y = center_y
                curr_z = center_z + radius * math.sin(angle)
                rot_y = (math.degrees(angle) + 90.0) % 360.0

                # PacketType.PlayerSync (10)
                payload = bytes([10]) + struct.pack('<Ifffffb', 2, curr_x, curr_y, curr_z, rot_y, 0.0, 1)
                s.sendall(struct.pack('<I', len(payload)) + payload)

            time.sleep(0.01)
    except KeyboardInterrupt:
        print("\n[*] Тест остановлен.")
    finally:
        s.close()
        print("[*] Бот отключен.")

if __name__ == '__main__':
    run_bot()
