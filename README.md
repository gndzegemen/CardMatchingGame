# Card Matching

Unity 6000.3.10f1; ana sahne: `Assets/Scenes/SampleScene.unity`.

## Süreli tek oyunculu oyun

Ana menüde **TIMED SOLO - 90s** ile başlar. İnternet veya sunucu gerektirmez. Süre, bütün kartların dağıtımı tamamlandıktan sonra başlar. Amaç 90 saniyede 9 çifti bulmaktır. Kalan süre ve bulunan çift sayısı oyun ekranında görünür.

- Süre bittiğinde kart seçimi kapanır ve sonuç ekranı açılır.
- Son çiftin ikinci kartı süre dolmadan seçilmişse, eşleşme animasyonu daha sonra bitse bile kazanılır.
- **Reset** yeni deste ve tam süreyle başlatır; **Menu** ana menüye döner.
- Süreyi Canvas üzerindeki `MemoryGame > Singleplayer > Single Player Duration` alanından değiştirebilirsiniz.

## Selfhosted multiplayer

Oyun sunucusu Unity Netcode + Unity Transport ile doğrudan WebSocket bağlantısı kabul eder. Her oturum iki oyunculudur; kart açma, sıra, skor ve yeniden başlatma kararlarını sunucu verir. Relay, Lobby veya başka bir bulut hesabı gerekmez.

### Bir oyuncunun sunucu olması

1. Masaüstü oyunda portu seçin (varsayılan **7777**) ve **HOST GAME** düğmesine basın.
2. Diğer oyuncu, host ekranında görünen LAN adresini (ör. `192.168.1.20:7777`) girip **JOIN GAME** düğmesine basar.
3. İkinci oyuncu bağlandığında oyun başlar.

Adres alanı IP, alan adı, `adres:port`, `ws://adres:port`, `wss://adres:port` veya `[IPv6]:port` kabul eder. Açıkça port içermeyen yalın adresler menüdeki portu kullanır. `ws://` için varsayılan 80, `wss://` için 443 kullanılır.

### Ayrı sunucu çalıştırmak

Unity Build Profiles üzerinden ana sahne dahil bir Windows veya Linux standalone build alın. Normal masaüstü build, aşağıdaki seçeneklerle grafik arayüzü olmadan sunucu olarak da çalışabilir. Windows örneği:

```powershell
.\CardMatchingGame.exe -batchmode -nographics -server -port 7777 -listen 0.0.0.0 -logFile server.log
```

Linux için aynı seçenekleri platformunuzun çalıştırılabilir dosyasına verin. Unity Dedicated Server build hedefi kullanılırsa sunucu modu kendiliğinden başlar; o platformun build modülünün Unity Hub üzerinden kurulmuş olması gerekir.

Ayrı sunucu oyuncu sayılmaz. İki oyuncu da sunucunun aynı adresine **JOIN GAME** ile bağlanır. Üçüncü oyuncu, oyun dolu mesajıyla reddedilir. Bir oyuncu ayrıldığında sunucu açık kalır; yeni oyuncu bağlanınca temiz bir tur başlar. Yeniden başlatmak için iki oyuncunun da **Reset** / yeniden oyna düğmesine basması gerekir.

Diğer başlatma seçenekleri:

```powershell
.\CardMatchingGame.exe -host -port 7777
.\CardMatchingGame.exe -connect 127.0.0.1:7777
```

`-server`, `-host` ve `-connect` seçeneklerinden yalnızca birini kullanın. `-listen` sunucunun dinleyeceği IP adresidir; varsayılan `0.0.0.0` tüm IPv4 ağ arayüzlerini kapsar. Yalnızca yerel makineden veya aynı makinedeki ters proxy üzerinden erişim için `-listen 127.0.0.1` kullanın.

### Tarayıcıda oda koduyla oynamak (masa tenisi gibi)

WebGL sürümünde sunucu gerekmez: oyunculardan birinin tarayıcısı host olur.

1. **CREATE ROOM** 5 karakterli bir oda kodu üretir ve panoya kopyalar.
2. Diğer oyuncu kodu adres alanına yazıp **JOIN GAME** ile katılır. `?room=KOD` içeren bir bağlantı kodu otomatik doldurur.
3. Bağlantı PeerJS ile kurulur, oyun trafiği iki tarayıcı arasında doğrudan WebRTC ile akar. Kurallar host tarayıcıda çalışır.

Adres alanına oda kodu yerine `wss://sunucu:port` yazılırsa tarayıcı, masaüstü/ayrı sunucuya WebSocket ile bağlanır.

### İnternet ve tarayıcı

- Doğrudan bağlantı için sunucunun **TCP** portuna ağ erişimi gerekir. Ev ağından internete sunucu açarken modem port yönlendirmesi ve işletim sistemi güvenlik duvarı ayarlarını bu porta göre yapın. CGNAT arkasında erişilebilir bir sunucu veya uygun bir ağ tüneli gerekir.
- HTTPS üzerinden açılan WebGL oyunu `wss://` bağlantısı ister. Kendi alan adınız ve geçerli TLS sertifikanızla bir WebSocket ters proxy kurup gelen WSS trafiğini sunucunun `ws://127.0.0.1:7777` adresine yönlendirin.
- WSS istemcisi sertifikayı doğrular. TLS sunucuda/ters proxyde ayrıca yapılandırılmalıdır; oyun sunucusu varsayılan olarak düz WebSocket dinler.
- Şu an tek sunucu süreci bir iki oyunculu oyun barındırır. Birden fazla oyun için farklı portlarda ayrı süreçler çalıştırın.

## Doğrulama

`Tests/GameplaySmoke.cs`, açık ana sahne Play Mode durumundayken KitWright `execute_code` ile çalıştırılabilen test komutudur. Zaman aşımı, eşleşme, son saniye kazanımı, dağıtımda çıkış, yeniden başlatma, adres doğrulama ve host/ayrı sunucu yaşam döngüsünü kontrol eder. Sonuç `UnityEditor.SessionState.GetString("CardMatching.Smoke", "")` ile okunur. Testler oyun ayarlarını sonunda geri yükler.
