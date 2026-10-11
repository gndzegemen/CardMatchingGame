# Card Matching

Unity 6000.3.10f1; ana sahne: `Assets/Scenes/SampleScene.unity`.

Ana menüde iki bölüm vardır: **SINGLE PLAYER** (süreli tek oyunculu oyun) ve **PLAY WITH A FRIEND** (iki kişilik oyun).

## Süreli tek oyunculu oyun

**SINGLE PLAYER > PLAY SOLO** ile başlar. İnternet veya sunucu gerektirmez. Süre, bütün kartların dağıtımı tamamlandıktan sonra başlar. Amaç 90 saniyede 9 çifti bulmaktır. Kalan süre ve bulunan çift sayısı oyun ekranında görünür.

- Süre bittiğinde kart seçimi kapanır ve sonuç ekranı açılır.
- Son çiftin ikinci kartı süre dolmadan seçilmişse, eşleşme animasyonu daha sonra bitse bile kazanılır.
- Oyun sırasında **RESTART** yeni deste ve tam süreyle başlatır; **MAIN MENU** ana menüye döner.
- Bitiş ekranında **PLAY AGAIN** yeni oyun başlatır, ev simgeli **MAIN MENU** ana menüye döner.
- Süreyi Canvas üzerindeki `MemoryGame > Singleplayer > Single Player Duration` alanından değiştirebilirsiniz.

## Arkadaşla oynamak (self-hosted)

Her oturum iki oyunculudur; kart açma, sıra, skor ve yeniden başlatma kararlarını oturumu açan taraf (host veya ayrı sunucu) verir. Unity Netcode kullanılır; Relay, Lobby veya bulut hesabı gerekmez.

### Tarayıcıda davet linkiyle (masa tenisi oyunundaki gibi)

WebGL sürümünde sunucu gerekmez: odayı kuran oyuncunun tarayıcısı oyunu barındırır.

1. **CREATE ROOM** 5 karakterli bir oda kodu üretir ve lobi ekranını açar.
2. **COPY INVITE LINK** davet linkini (`https://oyun-adresi/?room=K7P2Q`) panoya kopyalar. Telefonda düğme **SHARE INVITE LINK** olur ve paylaşım menüsünü (WhatsApp, Mesajlar vb.) açar.
3. Arkadaş linki açtığında oda kodu kendiliğinden dolar; **JOIN** ile katılır. Kod elle de yazılabilir; link yapıştırılırsa kod linkten alınır.
4. İkinci oyuncu katılınca oyun başlar. Biri ayrılırsa oda açık kalır ve kalan oyuncu lobide yeni oyuncu bekler.

Tarayıcılar, ücretsiz PeerJS aracı sunucusu üzerinden yalnızca tanışır; oyun trafiği iki tarayıcı arasında doğrudan WebRTC ile akar. Bunun için iki tarafın da internete erişebilmesi gerekir. Bazı kurumsal veya mobil ağlar doğrudan WebRTC bağlantısını engelleyebilir (TURN sunucusu kullanılmıyor).

Adres alanına oda kodu yerine `wss://sunucu:port` yazılırsa tarayıcı, masaüstü host'a veya ayrı sunucuya WebSocket ile bağlanır.

Development build'lerde otomatik test için `?autohost=K7P2Q` odayı bu kodla kurar, `?room=K7P2Q&autojoin=1` odaya kendiliğinden katılır. Bu kancalar normal build'lerde çalışmaz.

### Masaüstünde aynı ağda

1. Masaüstü oyunda **CREATE ROOM** ile host olun (port alanı varsayılan **7777**). Lobi ekranı bu bilgisayarın yerel ağ adresini (ör. `192.168.1.20:7777`) gösterir; **COPY ADDRESS** ile kopyalanır.
2. Diğer oyuncu bu adresi adres alanına yazıp **JOIN** düğmesine basar.
3. İkinci oyuncu bağlandığında oyun başlar.

Adres alanı IP, alan adı, `adres:port`, `ws://adres:port`, `wss://adres:port` veya `[IPv6]:port` kabul eder. Açıkça port içermeyen yalın adresler menüdeki portu kullanır. `ws://` için varsayılan 80, `wss://` için 443 kullanılır.

### Ayrı sunucu çalıştırmak

Unity Build Profiles üzerinden ana sahne dahil bir Windows veya Linux standalone build alın. Normal masaüstü build, aşağıdaki seçeneklerle grafik arayüzü olmadan sunucu olarak da çalışabilir. Windows örneği:

```powershell
.\CardMatchingGame.exe -batchmode -nographics -server -port 7777 -listen 0.0.0.0 -logFile server.log
```

Linux için aynı seçenekleri platformunuzun çalıştırılabilir dosyasına verin. Unity Dedicated Server build hedefi kullanılırsa sunucu modu kendiliğinden başlar; o platformun build modülünün Unity Hub üzerinden kurulmuş olması gerekir.

Ayrı sunucu oyuncu sayılmaz. İki oyuncu da sunucunun aynı adresine **JOIN** ile bağlanır. Üçüncü oyuncu, oyun dolu mesajıyla reddedilir. Bir oyuncu ayrıldığında sunucu açık kalır; yeni oyuncu bağlanınca temiz bir tur başlar. Yeniden başlatmak için iki oyuncunun da **RESTART** / **PLAY AGAIN** düğmesine basması gerekir.

Diğer başlatma seçenekleri:

```powershell
.\CardMatchingGame.exe -host -port 7777
.\CardMatchingGame.exe -connect 127.0.0.1:7777
```

`-server`, `-host` ve `-connect` seçeneklerinden yalnızca birini kullanın. `-listen` sunucunun dinleyeceği IP adresidir; varsayılan `0.0.0.0` tüm IPv4 ağ arayüzlerini kapsar. Yalnızca yerel makineden veya aynı makinedeki ters proxy üzerinden erişim için `-listen 127.0.0.1` kullanın.

### İnternet üzerinden sunucu

- Doğrudan bağlantı için sunucunun **TCP** portuna ağ erişimi gerekir. Ev ağından internete sunucu açarken modem port yönlendirmesi ve işletim sistemi güvenlik duvarı ayarlarını bu porta göre yapın. CGNAT arkasında erişilebilir bir sunucu veya uygun bir ağ tüneli gerekir.
- HTTPS üzerinden açılan WebGL oyunu `wss://` bağlantısı ister. Kendi alan adınız ve geçerli TLS sertifikanızla bir WebSocket ters proxy kurup gelen WSS trafiğini sunucunun `ws://127.0.0.1:7777` adresine yönlendirin.
- WSS istemcisi sertifikayı doğrular. TLS sunucuda/ters proxyde ayrıca yapılandırılmalıdır; oyun sunucusu varsayılan olarak düz WebSocket dinler.
- Şu an tek sunucu süreci bir iki oyunculu oyun barındırır. Birden fazla oyun için farklı portlarda ayrı süreçler çalıştırın.

## Doğrulama

`Tests/GameplaySmoke.cs`, açık ana sahne Play Mode durumundayken KitWright `execute_code` ile çalıştırılabilen test komutudur. Ana menü seçenekleri, oda kodu ve davet linki ayrıştırma, zaman aşımı, eşleşme, son saniye kazanımı, oyun ve bitiş ekranından ana menüye dönüş, dağıtımda çıkış, yeniden başlatma, adres doğrulama, lobi/iptal ve host/ayrı sunucu yaşam döngüsünü kontrol eder. Sonuç `UnityEditor.SessionState.GetString("CardMatching.Smoke", "")` ile okunur. Test çalışırken başka `execute_code` çağrısı yapmayın; derleme editörü kilitleyip süre testlerini bozabilir. Testler oyun ayarlarını sonunda geri yükler.
