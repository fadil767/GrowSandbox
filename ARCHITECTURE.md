# Arsitektur GrowSandbox

Dokumen ini merangkum struktur monorepo .NET 8 dan aturan arsitektur **wajib** yang harus dipatuhi oleh seluruh kontributor dan modul di dalam proyek **GrowSandbox**.

---

## Struktur Monorepo

```text
GrowSandbox/
├── GrowSandbox.sln
├── ARCHITECTURE.md
├── src/
│   ├── GrowSandbox.Shared/   # Class Library (.NET 8) — Single Source of Truth (fisika, tile, paket)
│   ├── GrowSandbox.Server/   # Console App (.NET 8) — Authoritative UDP Server (LiteNetLib)
│   └── GrowSandbox.Client/   # MonoGame DesktopGL (.NET 8) — Game Client (LiteNetLib)
└── tests/
    └── GrowSandbox.Tests/    # xUnit (.NET 8) — Unit & invariant tests terhadap Shared
```

### Aturan Ketergantungan Proyek

- `GrowSandbox.Shared` **tidak boleh** mereferensikan proyek lain (`Server`, `Client`, atau `Tests`) maupun library rendering/GUI.
- `GrowSandbox.Server` mereferensikan `GrowSandbox.Shared` dan menggunakan `LiteNetLib`.
- `GrowSandbox.Client` mereferensikan `GrowSandbox.Shared` dan menggunakan `MonoGame.Framework.DesktopGL` serta `LiteNetLib`.
- `GrowSandbox.Tests` mereferensikan `GrowSandbox.Shared` untuk memverifikasi kontrak data, ukuran memori, dan hukum fisika.

---

## Aturan Wajib Proyek

### 1. Authoritative Server Berbasis UDP (`LiteNetLib`)

- Seluruh simulasi permainan, validasi aksi pemain (gerakan, penempatan/penghancuran tile, interaksi dunia), dan kebenaran state (**source of truth**) berada sepenuhnya di `GrowSandbox.Server`.
- Komunikasi jaringan menggunakan protokol **UDP** melalui library **LiteNetLib** (`DeliveryMethod.ReliableOrdered`, `Unreliable`, atau `Sequenced` sesuai karakteristik paket).
- Klien (`GrowSandbox.Client`) hanya mengirimkan *input/intent* pemain dan melakukan prediksi/interpolasi visual lokal. Klien **dilarang** menentukan state permanen tanpa otorisasi server; server berhak menolak atau mengoreksi (reconcile) state klien kapan saja.

### 2. Penyimpanan Tile dalam Flat 1D Struct Array (`100 × 60`) Berukuran 8 Byte

- Setiap dunia berukuran tetap **100 kolom × 60 baris** (`6.000` tile total).
- Data tile **wajib** disimpan dalam array 1 dimensi datar (`Tile[6000]`) untuk memaksimalkan *CPU cache locality*, mencegah alokasi objek di *managed heap* (tanpa tekanan GC), dan mempermudah serialisasi snapshot jaringan.
- Pemetaan koordinat 2D `(x, y)` ke indeks 1D menggunakan rumus deterministik:
  ```text
  index = y * 100 + x
  ```
- Struct `Tile` (`Models/Tile.cs`) **wajib** berupa *value type* (`struct`) dengan ukuran memori tepat **8 byte** (`[StructLayout(LayoutKind.Sequential, Pack = 1)]`), sehingga satu dunia utuh hanya memakan `6.000 × 8 byte = 48.000 byte` (~46,9 KB):

| Field | Tipe | Ukuran | Keterangan |
| :--- | :--- | :---: | :--- |
| `ForegroundId` | `ushort` | 2 byte | ID blok/objek lapisan depan |
| `BackgroundId` | `ushort` | 2 byte | ID dinding/latar belakang |
| `Damage` | `byte` | 1 byte | Akumulasi kerusakan / progres hancur tile |
| `Flags` | `TileFlags` (`byte`) | 1 byte | Bitmask status tile |
| `ExtraDataId` | `ushort` | 2 byte | ID referensi metadata tambahan |
| **Total** | | **8 byte** | Diverifikasi melalui unit test (`Marshal.SizeOf<Tile>() == 8`) |

### 3. Pemrosesan Perintah Serial per `WorldInstance` Menggunakan `System.Threading.Channels`

- Setiap `WorldInstance` di sisi server bertindak sebagai *actor* terisolasi yang memiliki antrean perintah berbasis **`System.Threading.Channels.Channel<T>`** (dikonfigurasi dengan `SingleReader = true`).
- Thread jaringan (`LiteNetLib` poll loop) maupun sistem eksternal **tidak boleh** memutasi array `Tile[]` atau state entitas milik `WorldInstance` secara langsung. Semua mutasi wajib dikirim sebagai perintah (*command*) ke `ChannelWriter` milik `WorldInstance` tersebut.
- Satu loop konsumen serial membaca dan mengeksekusi perintah satu per satu dari `ChannelReader` sehingga tidak diperlukan `lock` berbutir halus (*fine-grained locking*), terhindar dari *race condition* dan *deadlock*, serta menjaga determinisme simulasi dunia.

### 4. Semua Konstanta Fisika dan Definisi Paket Wajib Berada di `GrowSandbox.Shared`

- Seluruh konstanta dunia & fisika (`Constants/WorldConstants.cs` dan `Constants/PhysicsConstants.cs`: dimensi dunia `100 × 60`, `TileSize = 32`, `Gravity`, `MoveSpeed`, `JumpForce`, `MaxReachTiles = 3`, *fixed tick rate*) **wajib** didefinisikan hanya di `GrowSandbox.Shared`.
- Seluruh definisi paket jaringan (`Networking/PacketType.cs`, `Networking/Packets/Packets.cs`) beserta helper serialisasi/deserialisasi `NetDataWriter`/`NetDataReader` (`Networking/PacketSerializer.cs`) **wajib** didefinisikan hanya di `GrowSandbox.Shared`.
- Duplikasi konstanta fisika atau definisi paket secara terpisah di `GrowSandbox.Server` maupun `GrowSandbox.Client` **dilarang keras**, guna menjamin sinkronisasi penuh antara simulasi otoritatif server dan prediksi klien.
