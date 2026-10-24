# Tweakstone

RivaTuner/Afterburner yeteneklerinde ama çok daha sade, kartına zarar vermemek için tasarlanmış, tek exe ile çalışan portable bir GPU ayar aracı.

## Çalıştırma

- `Tweakstone.exe`'yi istediğin klasöre koy ve çalıştır. Yönetici izni ister, çünkü NVIDIA'nın NVML API'si ayar yazmak için bunu şart koşuyor.
- Ayarlar, profiller ve logolar exe'nin yanındaki `GpuTweakData` klasöründe tutulur. Klasör yazılamazsa `%LOCALAPPDATA%\GpuTweak` kullanılır.
- NVIDIA kartı olmayan bir bilgisayarda arayüzü denemek için: `Tweakstone.exe --demo`
- Overlay'i aç/kapat: **Ctrl + Shift + O**
- **Simge alanı:** Pencerenin X tuşu programı kapatmaz, saatin yanındaki simge alanına gizler. Widget, kararlılık takibi ve uygulanan ayar çalışmaya devam eder. Simgenin üzerine gelince sıcaklık, güç ve aktif profil görünür. Tamamen kapatmak için simgeye sağ tıklayıp **Çıkış**.
- **Windows ile başlat** (Profillerim kartı): Ekran kartı ayarları her yeniden başlatmada sıfırlanır. Bu seçenek açıkken program oturum açılınca saatin yanındaki simge alanında başlar ve "Stabil" + "Otomatik: açık" profili yeniden uygular. Yönetici izni gerektiği için Görev Zamanlayıcı'ya "Tweakstone" adlı bir görev eklenir. Son açılan exe kullanılır.

## Güvenlik

| Koruma | Ne yapar |
|---|---|
| Aralık sınırlama | Her değer sürücünün bildirdiği vBIOS aralığına çekilir. Voltaj hiç değiştirilmez. |
| 15 sn onay | Uygula'dan sonra "Koru"ya basılmazsa önceki ayarlara otomatik dönülür. |
| Çökme algılama | Özel ayarlar açıkken uygulama/sistem beklenmedik kapanırsa, sonraki açılışta varsayılan ayarlarla başlar ve otomatik profil uygulanmaz. |
| Sıcaklık koruması | Özel ayarlar açıkken GPU 3 ölçüm üst üste 88°C'yi geçerse varsayılana dönülür. |
| Stabil profil şartı | Sadece "stabil" işaretlenmiş profil açılışta otomatik uygulanabilir. |

## Otomatik ayar (tatlı nokta)

"Otomatik ayar" kartında önce hedefi seç:

| Hedef | Ne yapar | Sonuç |
|---|---|---|
| UV tatlı nokta | Hız stok boost'ta kilitli, voltaj FPS düşmeden gidebildiği en alta iner | Aynı FPS, daha serin ve sessiz |
| Performans | Overclock: kilit yok, güç limiti vBIOS maksimumu, çekirdek offset artar | Biraz daha fazla FPS, biraz daha sıcak |
| Verimli | Hız stok boost'un %6 altında kilitli, voltaj düşer | FPS biraz düşer, güç ve ısı belirgin azalır |

**Başlat** düğmesi kartı kendi kendine test eder (~20 dk). Aşağıdaki adımlar "UV tatlı nokta" içindir; diğer hedefler aynı hata kontrolünü kullanır:

1. Stok ayarlarda, hesap sonuçlarını kendisi doğrulayan bir DirectX 11 yükü çalıştırır ve referans sonuçları kaydeder.
2. Kartın stokta ulaştığı en yüksek boost hızını ölçer ve max clock'u oraya kilitler, böylece hiçbir oyunda stoktan yavaş çalışmaz.
3. Çekirdek offset'i 30 MHz'lik adımlarla artırır, yani aynı hızda voltajı düşürür. Her adım 1 dakika ağır ve dalgalı yükle test edilir. Tek bir hesap hatası, sürücü sıfırlanması, 85°C üstü sıcaklık ya da %1'den fazla performans kaybı görülürse adım başarısız sayılır.
4. Son geçen değerden 45 MHz güvenlik payı geri çekilir ve sonuç 5 dakika doğrulanır.
5. Sonuç uygulanır ve "Otomatik UV" profili olarak kaydedilir. Birkaç gün oyunlarda sorunsuz giderse profili "Stabil" olarak işaretle.

Test sırasında bilgisayar kilitlenirse bir sonraki açılışta program stok ayarlarla başlar. "Kaldığı yerden devam" denince kilitlenen değer kararsız sayılır.

## Bellek sıcaklığı

Bellek sıcaklığı sadece kart bunu NVIDIA'nın resmi arayüzü NVML'e veriyorsa gösterilir. Çoğu GeForce kart, RTX 5080 de dahil, bu değeri vermez (`nvidia-smi` "N/A" gösterir). O zaman satır görünmez. Belgelenmemiş NVAPI ile okumayı v0.11.1'de denedik. Windows Defender o sürümü yanlış alarmla engellediği için bu özellik kaldırıldı.

## Bellek OC

Otomatik ayardaki **Bellek OC** hedefi, şu anki çekirdek ayarına dokunmadan bellek hızını artırır (yaklaşık 13 dakika):

1. 2 × 256 MB'lık iki tampon sürekli okunup yazılır. Her değer, sırası ve geçiş numarasından hesaplanan beklenen değerle kontrol edilir. Bu sayede bellek hatası hemen yakalanır. Ölçülen hız GB/s olarak raporlanır.
2. Bellek offset'i, sürücünün izin verdiği aralığın onda biri kadar adımlarla artırılır.
3. İlk hatada, sürücü sıfırlanmasında ya da bant genişliği düşünce durur. GDDR7 hatalı aktarımları tekrar ettiği için kararsızlık önce hız kaybı olarak görünür. Bant genişliği iki adım üst üste artmazsa da durur.
4. En hızlı ölçülen değerden bir adım geri gelinir, 3 dk bellek + 2 dk karışık yükle doğrulanır. Sonuç aktif profile eklenir.

Not: Afterburner'daki bellek değeri ile NVML'in birimi farklı olabilir. Karşılaştırmak için sonuçtaki bant genişliği artış yüzdesine bak.

## Doğrulama testi

**Doğrulama testi** düğmesi, şu an karttaki ayarları yaklaşık 32 dakika test eder (FurMark gibi), ama her hesabın sonucunu da kontrol eder. Üç bölümü var:

1. **Sürekli yük** (15 dk): kart ısınır, en yüksek hızında çalışır.
2. **Ani yük** (8 dk): kısa yük patlamaları ve boşluklar; oyun menüleri ve yükleme ekranlarındaki gibi hız ve akım sıçramaları.
3. **Eğri taraması** (6 × 90 sn): kart 1200 MHz'den en yüksek hızına kadar farklı hızlarda tutulur. Çekirdek offset'i voltaj eğrisinin her noktasını düşürür, ama tam yük sadece en üst noktayı çalıştırır; bu bölüm alttaki noktaları da sınar.

Hesap hatası veya sürücü sıfırlanması olursa çekirdek ayarı 30 MHz düşürülür ve aynı bölüm tekrarlanır (en fazla 4 kez). Test geçerse düşürülmüş ayar kalır ve onu kullanan profil de güncellenir. 85°C'de test durur. Dört düşürmeden sonra hâlâ hata varsa kart varsayılan ayarlara döner.

## Oyun kararlılığı

Program açıkken oyunları kendiliğinden algılar: GPU 30 saniye boyunca yoğun çalışırsa öndeki program oyun sayılır. Her 30 saniyede Windows olay kayıtlarına bakar:

| Kayıt | Anlamı |
|---|---|
| Display 4101, nvlddmkm 13/14/153 | Ekran sürücüsü yanıt vermedi ve sıfırlandı |
| Application Error 1000 / Hang 1002 | Bilinen bir oyun çöktü veya dondu |
| Kernel-Power 41, BugCheck 1001 | Bilgisayar beklenmedik şekilde kapandı / mavi ekran |

Özel ayarlar açıkken bunlardan biri olursa ayar bir adım güvenli tarafa çekilir (çekirdek −30 MHz, sonra bellek −250 MHz, sonra kilit ve güç limiti fabrika değerine) ve aynı ayarları tutan profiller de güncellenir. Oyunda titreyen doku, renkli nokta gibi bir bozulma görürsen **Artifact gördüm** düğmesine veya oyundayken **Ctrl+Shift+A**'ya bas: bu durumda önce bellek offset'i düşürülür. Kart varsayılan ayardayken olan sorunlar sadece kaydedilir. Beklenmedik yeniden başlatma ve mavi ekran ancak ekran kartı o sırada yük altındaysa sayılır: program çalışırken son durumu (GPU boşta mı, oyunda mı) birkaç saniyede bir kaydeder, kart masaüstünde boştayken olan bir yeniden başlatma (başka bir program, elektrik kesintisi, Windows güncellemesi) "sayılmadı" olarak kaydedilir ve ayar değişmez. Yine de yanlış bir düzeltme olursa **Son düzeltmeyi geri al** ile önceki ayara dönülür. Aynı ayarlarla 10 saat sorunsuz oyundan sonra profili "Stabil" işaretleme önerilir. Kayıtlar `stability.json` dosyasındadır.

## Silikon skoru

Otomatik ayar bitince, çipin stok boost hızında hatasız kaldırdığı en yüksek çekirdek offset'i, günlük kullanılan değer ve oyunlarda kararlı kalan değer gösterilir. Bu, tahmini bir "ASIC kalitesi" değil, senin kartında ölçülen sonuçtur.

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
- [x] Otomatik ayar: hata doğrulamalı stres testiyle undervolt tatlı noktası
- [x] Arayüz pencere boyutuna göre ölçeklenir, kaydırma gerekmez
- [x] Canlı test paneli: animasyonlu test görüntüsü ve "Bulunan artifact" sayacı, canlı grafikler, adım sonuçları
- [x] Widget'ta FPS: şu an / min / ort / maks (ETW Present olayları, enjeksiyonsuz). Bellek sıcaklığı (kart veriyorsa)
- [ ] 1% low
- [ ] Kaynaklı topluluk önerileri veritabanını doldurmak
- [ ] AMD (ADLX)
- [x] Windows ile başlatma
- [x] Oyun kararlılığı takibi (Windows olay kayıtları) ve otomatik geri çekme, silikon skoru
- [x] GPU sıcaklığı Min / Ort / Maks
- [x] Dil seçimi (Türkçe / English), üst köşedeki TR | EN düğmesi
- [x] Voltaj eğrisinin alt noktalarını da sınayan doğrulama testi
- [x] Güvenli bellek overclock'u (bant genişliği ölçümüyle)
- [ ] DX12 ışın izleme (DXR) stres testi
- [ ] Fan eğrisi, sistem tepsisi
- [ ] (Deneysel) V/F eğrisi editörü
