# TLD Ortak Dünya (Shared World)

**The Long Dark için co-op modu: arkadaşlarınla gerçekten ortak tek bir dünyada oyna.**

[English](README.md) · [İndir](../../releases) · [Bilinen eksikler](#bilinen-eksikler) · [Geri bildirim](#geri-bildirim) · [Modu destekle](#modu-destekle)

> **Deneme sürümü.** Mod çalışıyor ve birçok test turundan geçti, ama hâlâ yeni.
> Denemeden önce kayıtlarını yedekle ve bozulan bir şey olursa lütfen [bize yaz](#geri-bildirim).

Çoğu co-op modunda her oyuncunun haritası ayrı bir kopyadır. Burada dünya ortak: arkadaşın çiftlik
evindeki tüfeği alırsa senin için de yok olur. Birbirinizi görürsünüz, aynı geyiği avlar, aynı kurttan
kaçar, aynı geceyi uyuyarak geçirirsiniz.

## Neler var

- **Tek dünya.** Eşyalar, dolaplar, ateşler, leşler ve hayvanlar herkes için aynı. Dünya sunucuyu
  açanın kaydıdır; katılan arkadaş onun bir kopyasını alır ve onda oynar.
- **Arkadaşını görürsün.** Arkadaşın yürüyen, çömelen, uyuyan, yere yatan, meşale ya da işaret fişeği
  tutan bir hayatta kalan olarak görünür. Ne giyiyorsa ona göre görünür (bere, atkı, eldiven ve renkler).
- **Av ve vahşi hayvanlar.** Kurtlar ve ayılar en yakındaki oyuncunun peşine düşer, bunu herkes görür.
  Birinizin vurduğu geyiği diğeriniz parçalayabilir.
- **Ölmek yok, yere yığılmak var.** Kondisyonun sıfıra inince ölmek yerine yere yığılırsın. Arkadaşın
  10 dakika içinde seni kaldırabilir (F3 basılı). Kimse gelmezse güvenli bir yerde kendine gelirsin,
  eşyaların düştüğün yerde kalır.
- **Birlikte uyku.** Zaman yalnızca herkes uyurken ya da zaman geçirirken hızlanır.
- **Uzun işler gerçek süre alır.** Et çıkarma, alet yapma ya da okuma ortak saati atlatmaz; süresi dolana
  kadar başında kalırsın (F8 beklemeden devam eder).
- **Yakın mesafe sesli konuşma.** Bas-konuş (T). Yaklaştıkça ses yükselir, ses arkadaşının olduğu yönden gelir.
- **Eşya verme.** Arkadaşına dön ve V'ye bas: elindekini verirsin. Envanterde bir eşya seçip V'ye basarak da verebilirsin.
- **Islık.** B'ye bas; arkadaşın duyar ve sesin aşağı yukarı nereden geldiği söylenir.
- **Ortak kömür haritası.** Birinizin haritaya çizdiği yer diğerinin haritasında da görünür.
- **Dost ateşi** açık: tüfek, tabanca, işaret fişeği tabancası ve ok arkadaşını yaralar.
- **Oyunun kendi tarzında menüler.** Her şey ana menüde ve Esc menüsünde **ORTAK OYUN** içinde.
  Oyun Türkçeyse mod Türkçe, değilse İngilizce konuşur.

## Gerekenler

- The Long Dark (Steam ya da Epic), **Hayatta Kalma modu**. Herkeste **aynı oyun sürümü** ve **aynı mod sürümü** olmalı.
- [MelonLoader](https://github.com/LavaGang/MelonLoader.Installer/releases) **0.7.3**.
- Windows. Konuşmak istersen mikrofon.
- En fazla 4 oyuncu (sen dahil).

## Kurulum

1. MelonLoader kurucusuyla The Long Dark'a MelonLoader 0.7.3'ü kur, sonra oyunu bir kez açıp kapat
   (ilk açılış birkaç dakika sürebilir).
2. [Sürümler](../../releases) sayfasından `TLD-Shared-World-<sürüm>.zip` dosyasını indirip aç.
3. Oyunu kapat ve `install.bat` dosyasına çift tıkla (Steam ve Epic kurulumunu kendisi bulur, önce sorar).
   Elle: `Mods\TLDCoop.dll` dosyasını oyunun `Mods` klasörüne, `UserLibs` içindeki üç DLL'i oyunun
   `UserLibs` klasörüne kopyala.
4. Oyunu aç. MelonLoader penceresinde **TLD Shared World (Ortak Dunya)** yazısını görmelisin.

## Oynamak

**Sunucuyu açan (dünyanın sahibi)**
1. Kendi Hayatta Kalma kaydını yükle.
2. Esc → **ORTAK OYUN** → **SUNUCU AÇ**.
3. Aynı menüde adresin görünür: başka evdeki arkadaş için **İNTERNET: …**, aynı modeme bağlı arkadaş
   için **EV AĞI: …**. Seçince kopyalanır; arkadaşına gönder.

**Katılan arkadaş**
1. Arkadaşının gönderdiği adresi kopyala.
2. Ana menü → **ORTAK OYUN** → **KATIL (PANODAKİ): …** (ya da **ADRESİ YAZ**).
3. **ORTAK DÜNYA HAZIR** yazısını bekle, sonra Hayatta Kalma → Yükle'yi aç. **ORTAK DUNYA** kaydı kendiliğinden açılır.

**İnternet üzerinden:** sunucu açılınca mod modemden UDP 27500 kapısını açmasını ister (UPnP). Menü
modemin açmadığını söylerse modemde UPnP'yi aç ya da UDP 27500'ü bilgisayarına yönlendir. Bu mümkün
değilse (bazı internet sağlayıcıları izin vermez) iki bilgisayara da aynı VPN'i kur (Radmin VPN,
Tailscale ya da Hamachi) ve onun verdiği adresi kullan.

## Tuşlar

| Tuş | Ne yapar |
|---|---|
| Esc → ORTAK OYUN | sunucu aç, katıl, adres kopyala, ayrıl, geri bildirim |
| F9 / F10 / F11 | kısayol: sunucu aç / katıl / ayrıl |
| F6 | durum, adresin ve tuş listesi |
| F3 | yerdeki arkadaşın yanında 5 sn basılı tut: kaldırır; yerdeyken 3 sn: vazgeçersin |
| F8 | diğerlerinin uyumasını beklerken kalk; uzun işi beklemeden bitir |
| T | bas-konuş |
| B | ıslık |
| V | elindekini (ya da envanterde seçili olanı) ver |

B ya da V oyunda başka bir işe ayarlıysa mod boş bir tuşa geçer ve hangisi olduğunu söyler.

## Ayarlar

Ayarlar `<oyun klasörü>\UserData\MelonPreferences.cfg` dosyasında, `[TLDCoop]` başlığı altında (her
satırın İngilizce ve Türkçe açıklaması var). İşe yarayanlardan bazıları:

| Ayar | Varsayılan | |
|---|---|---|
| `Dil` | `auto` | dil: `auto`, `en` ya da `tr` |
| `OyuncuAdi` | Windows kullanıcı adın | arkadaşlarının gördüğü ad |
| `DostAtesi` | `true` | dost ateşi |
| `IsSuresi` | `true` | uzun işler gerçek ortak süre alır |
| `SesliKonusma` / `SesMenzili` | `true` / `40` | sesli konuşma ve metre cinsinden menzili |
| `KonusTusu` / `IslikTusu` / `VerTusu` | `T` / `B` / `V` | konuşma, ıslık ve verme tuşları |
| `YereYigilma` / `YerdeKalmaDakika` | `true` / `10` | yere yığılma ve kaldırılmayı bekleme süresi |
| `KiyafetGorunumu` | `true` | arkadaşının figürü gerçekten giydiklerini gösterir |
| `Port` / `OtomatikPortAc` | `27500` / `true` | sunucu kapısı ve otomatik kapı açma (UPnP) |
| `MaksOyuncu` | `4` | sunucudaki en fazla oyuncu |

## Bilinen eksikler

Bu, küçük bir stüdyonun hazırladığı bir deneme sürümü. Bildiğimiz eksikler şunlar:

- **Çoğunlukla tek bilgisayarda denendi.** Testlerin çoğu aynı bilgisayarda yan yana açılan iki oyunla
  yapıldı. İki ayrı evden internet üzerinden tam bir oturum henüz yapılmadı; bağlantı sorunları olabilir.
- **2 oyuncuyla denendi.** Sunucu 4 kişi alıyor; uyku, yere yığılma ve hayvanlar ikiden fazla kişiye göre
  yazıldı, ama 3 ve 4 kişilik oyun henüz denenmedi.
- **Şifre yok.** Sunucun açıkken adresini bilen ve modu kurmuş herkes (oyuncu sınırına kadar) katılabilir.
  Adresi yalnızca arkadaşlarınla paylaş ve işin bitince sunucuyu kapat.
- **İnternet için kapı açık olmalı.** UPnP ya da port yönlendirme (veya VPN) olmadan başka evdeki arkadaş bağlanamaz.
- **Sürümler aynı olmalı.** Farklı oyun ya da mod sürümleri reddedilir. Bir oyun güncellemesi, yeni mod
  sürümü çıkana kadar modu bozabilir.
- **Yalnızca Hayatta Kalma modu.** Wintermute hikâye modu desteklenmiyor; meydan okumalar ve Tales denenmedi.
- **Dünya sunucunun kaydında durur.** Sunucuyu açan yokken oynanacak ortak dünya da yoktur.
- **Uzun işler gerçek zamanla bekletir.** 2 saatlik bir parçalama seni yaklaşık 10 gerçek dakika meşgul
  eder (F8 beklemeyi atlar, ama o zaman işin süresi dünyaya sayılmaz). Bu oyunun zaman hızlandırmasıyla
  denendi; her iş türüyle elle henüz denenmedi.
- **Arkadaşının figürü modun kendi hayatta kalan modelidir**, oyunun karakterleri değildir. Kıyafetler
  türüne ve rengine göre gösterilir, hareketler basittir.
- **Sesli konuşma** Windows'un varsayılan mikrofonunu ve hoparlörünü kullanır; yankı ya da gürültü
  süzgeci yoktur ve cihaz oyunda seçilemez.
- **Adres kutusuna Ctrl+V ile yapıştırma** denenmedi; menüdeki *KATIL (PANODAKİ)* aynı işi görür ve denendi.
- **Oyun içinde ayar ekranı yok**: ayarlar `MelonPreferences.cfg` dosyasından değiştirilir.
- Mod yalnızca **Türkçe ve İngilizce**.

## Geri bildirim

Bir hata buldun ya da bir fikrin mi var? **destek@topraklikgamedevstudio.com** adresine yaz (oyunda:
ORTAK OYUN → GERİ BİLDİRİM adresi kopyalar) ya da bir [konu (issue)](../../issues) aç.

Lütfen şunları ekle:
- mod sürümü, oyun sürümü, Steam mi Epic mi;
- sunucuyu açan mıydın katılan mı, sorundan hemen önce ne oldu;
- günlük dosyaları: `<oyun klasörü>\MelonLoader\Latest.log` ve `<oyun klasörü>\UserData\TLDCoop-*.log` (en yenisi).

## Modu destekle

Mod ücretsiz ve herkes için ücretsiz kalacak. Beğendiysen ve teşekkür etmek istersen bu sayfadaki
**Sponsor** düğmesiyle Topraklık Game Dev Studio'yu destekleyebilirsin. Bağış isteğe bağlıdır ve hiçbir
şeyin kilidini açmaz; herkes aynı modu alır.

## Kaynaktan derlemek

Adımlar [İngilizce README](README.md#building-from-source) dosyasında.

## Emeği geçenler ve lisanslar

- **Topraklık Game Dev Studio** tarafından yapıldı. [GNU GPL v3](LICENSE) lisanslıdır: kullanabilir,
  değiştirebilir ve paylaşabilirsin; paylaştığın şey de kaynağıyla birlikte aynı lisansla kalmalıdır.
- Ağ için [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT), ses için
  [NAudio](https://github.com/naudio/NAudio) (MIT, © Mark Heath); lisansları `package/licenses` klasöründe ve indirmede.
- [MelonLoader](https://github.com/LavaGang/MelonLoader) ve Harmony üzerinde çalışır.

Bu resmî olmayan, hayranların yaptığı bir moddur. Hinterland Studio tarafından yapılmamış, onaylanmamış
ve desteklenmemektedir. The Long Dark, Hinterland Studio Inc.'in ticari markasıdır. Oyunun kendi kopyan gerekir.
