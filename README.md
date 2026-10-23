# GPU Tweak

RivaTuner/Afterburner yeteneklerinde ama çok daha sade, kartına zarar vermemek için tasarlanmış, tek exe ile çalışan portable bir GPU ayar aracı.

## Çalıştırma

- `GpuTweak.exe`'yi istediğin klasöre koy ve çalıştır. Yönetici izni ister, çünkü NVIDIA'nın NVML API'si ayar yazmak için bunu şart koşuyor.
- Ayarlar, profiller ve logolar exe'nin yanındaki `GpuTweakData` klasöründe tutulur. Klasör yazılamazsa `%LOCALAPPDATA%\GpuTweak` kullanılır.
- NVIDIA kartı olmayan bir bilgisayarda arayüzü denemek için: `GpuTweak.exe --demo`
- Overlay'i aç/kapat: **Ctrl + Shift + O**

## Güvenlik

| Koruma | Ne yapar |
|---|---|
| Aralık sınırlama | Her değer sürücünün bildirdiği vBIOS aralığına çekilir. Voltaj hiç değiştirilmez. |
| 15 sn onay | Uygula'dan sonra "Koru"ya basılmazsa önceki ayarlara otomatik dönülür. |
| Çökme algılama | Özel ayarlar açıkken uygulama/sistem beklenmedik kapanırsa, sonraki açılışta varsayılan ayarlarla başlar ve otomatik profil uygulanmaz. |
| Sıcaklık koruması | Özel ayarlar açıkken GPU 3 ölçüm üst üste 88°C'yi geçerse varsayılana dönülür. |
| Stabil profil şartı | Sadece "stabil" işaretlenmiş profil açılışta otomatik uygulanabilir. |

## Undervolt nasıl yapılır (NVIDIA)

1. "Undervolt (max clock kilidi)" anahtarını aç ve max clock'u kartının oyunda gördüğün boost değerine yakın bir yere koy.
2. Çekirdek offset'i küçük adımlarla (+15 / +30 MHz) artır, her adımda Uygula → test et → Koru.
3. Kart aynı frekansı daha düşük voltajla çalıştırır: daha düşük sıcaklık ve güç. Çökme olursa offset'i bir adım geri al.

## Overlay logosu

NVIDIA/AMD logoları ticari marka olduğu için exe içinde gelmez; widget'ta marka adı renkli yazı olarak görünür. Kendi logonu kullanmak için `GpuTweakData\logos\nvidia.png` (veya `amd.png`) dosyasını koy.

## Topluluk önerileri

`src/GpuTweak.Core/Profiles/community-profiles.json` exe'ye gömülür. Güncel bir dosyayı `GpuTweakData\community-profiles.json` olarak koyarsan o kullanılır. Kaynağı (`https` linki) olmayan girdiler reddedilir. Öneriler önce yarı güçte (offset'ler %50) yüklenir.

```json
{
  "schemaVersion": 1,
  "entries": [
    {
      "gpuMatch": "RTX 5080",
      "title": "Kısa açıklama",
      "kind": "undervolt",
      "settings": { "coreOffsetMHz": 0, "memoryOffsetMHz": 0, "powerLimitW": null, "maxCoreClockMHz": null },
      "sourceUrl": "https://...",
      "sourceTitle": "Kaynak adı",
      "retrievedAt": "2026-10-23"
    }
  ]
}
```

## Geliştirme

- .NET 8, WPF. `dotnet test` çekirdek testlerini çalıştırır (Linux'ta da çalışır).
- Tek exe: `dotnet publish src/GpuTweak.App -c Release -o publish`

## Yol haritası

- [x] NVIDIA: telemetri, clock/bellek offset, güç limiti, max clock kilidi (undervolt), fan
- [x] Güvenlik katmanı, profiller, overlay widget, Apple tarzı arayüz
- [ ] FPS ve 1% low (PresentMon / ETW, enjeksiyonsuz)
- [ ] Kaynaklı topluluk önerileri veritabanını doldurmak
- [ ] AMD (ADLX)
- [ ] Fan eğrisi, sistem tepsisi, Windows ile başlatma
- [ ] (Deneysel) V/F eğrisi editörü
