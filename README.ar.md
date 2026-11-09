<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  بيئة تطوير ويب محلية سريعة ومحمولة ومفتوحة المصدر لنظام Windows<br>
  <sub>من <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · بلا إعلانات ولا تتبّع</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (BR)</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <b>العربية</b> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>التطبيق نفسه يدعم 31 لغة (العربية والفارسية والأردية من اليمين إلى اليسار).</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="الخدمات"> <img src="assets/screenshots/en-projects.png" width="49%" alt="المشاريع"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="افتح مشروعاً على هاتفك برمز QR"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="مشروع جديد"></p>

يشغّل Portly خادم Apache أو Nginx، وPHP، وMySQL أو MariaDB، وPostgreSQL، وMongoDB، وRedis، وMemcached، وNode.js، وMailpit، وComposer، وAdminer وphpMyAdmin من مجلد واحد. لا يحتاج إلى مثبّت، ولا يُثبَّت شيء في Windows افتراضياً: لا خدمات Windows (وضع الخدمة الاختياري، المعطَّل ما لم تفعّله من الإعدادات > الميزات، يمكنه تشغيل المكدس كخدمة)، ولا فوضى في السجل. يعمل حتى من عصا USB.

## لماذا Portly؟

| | Laragon | **Portly** |
|---|---|---|
| الترخيص | مغلق المصدر، ومدفوع للاستخدام التجاري | **MIT، مجاني تماماً، بلا إعلانات** |
| تحديثات التطبيق | تنزيل مثبّت جديد | **نقرة واحدة داخل التطبيق** (مع التحقق بـ SHA-256) |
| قائمة الحزم | ملف `packages.conf` محلي | **كتالوج يُحدَّث عن بُعد** + إصدارات PHP الحية من windows.php.net |
| إصدار PHP لكل موقع | لا | **نعم** (كل إصدار PHP على منفذ FastCGI خاص به) |
| HTTPS | شهادة واحدة تُعاد إنشاؤها لكل مشروع | **شهادة جذر محلية موثوقة مرة واحدة**، وشهادة لكل موقع |
| أمان الشبكة | يستمع على كل الواجهات | **127.0.0.1 افتراضياً** (بلا نافذة من جدار الحماية) |
| تقوية PHP | الإعدادات الافتراضية | `session.upload_progress` و`allow_url_include` **معطّلان** |
| أمان الانهيار | — | إذا توقف Portly توقف Windows الخوادم التي بدأها (job object) |
| تعارض المنافذ | رسالة خطأ | يعرض البرنامج الذي يشغل المنفذ؛ **التبديل إلى منفذ حر بنقرة واحدة** أو إغلاق ذلك البرنامج |
| المشاركة | ngrok (يلزم حساب) | **الشبكة المحلية** (الهاتف أو الجهاز اللوحي على نفس شبكة الواي فاي، برمز QR، وhttps، وكلمة مرور) و**الإنترنت** عبر Cloudflare: عنوان عشوائي بلا حساب، أو **عنوان ثابت على نطاقك الخاص** |
| النقل | — | مشاريع وقواعد بيانات من **Laragon وXAMPP وWampServer وAMPPS وUwAmp وUniform Server وOpen Server وEasyPHP وMAMP** (ومشاريع من Herd وLocal) |
| قواعد البيانات | MySQL | **MySQL أو MariaDB، وPostgreSQL، وMongoDB، وSQLite**؛ وRedis وMemcached |
| أدوات قواعد البيانات | HeidiSQL | **phpMyAdmin** (يُفتح مسجَّل الدخول طالما أن مستخدم root بلا كلمة مرور)، **Adminer، وHeidiSQL، وDBeaver، وBeekeeper Studio، وDbGate، وDB Browser for SQLite** |
| WordPress | يدوي | **WordPress بنقرة واحدة** (WP-CLI، ومستخدم مدير جاهز) |
| اللغات | 34 (txt) | **31**، ملفات JSON عادية، ومفتوحة للمساهمات |
| Windows 7 / 8 / 8.1 | غير واضح | **إصدار Legacy منفصل** |

## الميزات

- **المشاريع**: كل مجلد في `www` يصبح `https://name.portly`؛ ويُكتشف مجلد `public` بأسلوب Laravel. تجميع المجلدات والمشاريع الفرعية (`blog.shop.portly`)، وقوالب لـ Laravel وSymfony وCodeIgniter وCakePHP وDrupal وVue وReact وSvelte وNuxt وNext.js وAngular وAstro. تُظهر كل بطاقة بماذا بُني المشروع وقاعدة بياناته.
- **مشاركة مشروع**
  - *الشبكة المحلية*: افتحه على هاتفك برمز QR. يعمل عبر https؛ ويرشدك دليل على الهاتف لتثبيت شهادة Portly على iPhone وAndroid مرة واحدة. كلمة مرور اختيارية، ومدة زمنية، والتشغيل مع Portly، وقائمة الأجهزة المتصلة؛ وتظهر أخطاء PHP القادمة من الهاتف في سجل Portly.
  - *الإنترنت*: نفق سريع مجاني من Cloudflare (بلا حساب)، أو نطاقك الخاص مثل `demo.example.com` عبر حسابك في Cloudflare. نفس كلمة المرور والمدة الزمنية وقائمة الأجهزة.
- **إصدار PHP لكل موقع**، و**إضافات PHP** بنقرة واحدة (17 إضافة PECL، متحقَّق منها لكل إصدار PHP)، و**Xdebug**، والطرفيات التي تتبع إصدار PHP وNode للمشروع (`.php-version` و`composer.json` و`.nvmrc`).
- **الخدمات**: تشغيل الكل / إيقاف الكل مع قفل لكل خدمة (اختر الخدمات التي تنتمي إلى المجموعة)، وMySQL / MariaDB في بطاقة واحدة، وTomcat + Java اختيارياً.
- **إشعارات التحديث** ("PHP 8.6 صدر" ← التثبيت والتبديل بنقرة واحدة)، وتحذيرات نهاية الدعم وCVE.
- **أمان قواعد البيانات**: فحص وإصلاح، ونقل آمن بين إصدارات MySQL / MariaDB، واستيراد SQL كبير، ونسخ احتياطي تلقائي، وإعادة تعيين كلمة مرور root المنسية، ووضع استرداد InnoDB.
- **سير عمل المطور**: مهام في الخلفية (queue وscheduler وVite)، وسجلات المواقع، ومُحلِّل الأداء، وأداة سطر الأوامر `portly` مع hooks، وملف المشروع `portly.yml`، والدفع والسحب عبر SSH.
- **المواقع**: نطاقات إضافية ونطاقات عامة (wildcard)، وشهاداتك الخاصة، والأرشفة، وإصلاح SSL، وثقة شهادات Firefox وJava، و**Caddy** و**FrankenPHP** (Octane).
- **الوقاية من المشكلات**: مجلدات OneDrive، والمسارات غير اللاتينية والطويلة، وPHP ثانٍ أو PHPRC، ومضاد الفيروسات الذي يحجب ملف hosts، وفحص الحجر الصحي.
- واجهة شريط جانبي حديثة مع سمة فاتحة / داكنة تتبع إعدادات Windows، ودعم DPI لكل شاشة، ودعم قارئ الشاشة. تُفعَّل الميزات قليلة الاستخدام من الإعدادات > الميزات.

## الإصدارات

| التنزيل | Windows | التحديثات | الحزم |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11 (64 بت) | **نعم**: التطبيق + كتالوج الحزم | الأحدث: PHP 8.5، MySQL 8.4 LTS / 26.x، Apache 2.4، Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64 بت) | **لا** (كتالوج مجمّد) | آخر الإصدارات التي تعمل على Windows 7: PHP 8.2، MySQL 5.7، Apache 2.4 VS16، Node 13 |
| macOS / Linux | — | — | **قريباً.** صادرات الإعدادات مستقلة عن المنصة أصلاً. ضع نجمة للمستودع أو ادعم المشروع للمساعدة في تحقيقه. |

تحتاج نسخة Legacy إلى .NET Framework 4.8 (وهو موجود أصلاً في Windows 10 / 11).

## البدء السريع

1. نزّل ملف zip من [Releases](https://github.com/erdemsoftware/portly/releases) وفكّ ضغطه في مجلد **بدون مسافات**، مثل `C:\Portly`.
2. شغّل `Portly.exe`. تُحدَّد الحزم الموصى بها تلقائياً عند أول تشغيل: انقر **تثبيت المحدد**.
3. انقر **تشغيل الكل**.
4. أنشئ مجلداً في `www`: يصبح `www\shop` هو `https://shop.portly`. يُفتح المجلد الذي يحمل اسم نطاق (`www\example.com`) على `https://example.com`، فتبقى العناوين كما هي عند النشر على الإنترنت؛ ويعيد مفتاح Local / Live على بطاقته الموقع الحي إلى جهازك، بينما تستمر النسخة المحلية في العمل على `https://example.com.portly`.

يفتح `http://localhost` لوحة تحكم بكل المواقع وAdminer وMailpit و`phpinfo()`. للمزيد راجع [docs/04-kullanim.md](docs/04-kullanim.md) (بالتركية).

```
Portly\
├─ Portly.exe            the app
├─ lang\  templates\     languages and config templates (renewed by updates)
├─ bin\                  packages: bin\php\php-8.5.12-..., bin\mysql\mysql-8.4.11-winx64 ...
├─ etc\                  generated configuration (rewritten at every start)
├─ usr\                  your settings, own templates, SSL authority (never touched by updates)
├─ data\                 databases (one folder per MySQL series)
├─ www\                  your projects (another folder can be chosen in Settings)
└─ logs\  tmp\  backup\
```

لتغيير قالب إعدادات، انسخه من `templates\` إلى `usr\templates\` وعدّل النسخة؛ لن تمسّها التحديثات.

## البناء من المصدر

المتطلبات: أي .NET SDK 6 أو أحدث (يستهدف التطبيق .NET Framework 4.8، لذا يعمل على Windows 7 SP1 – 11 دون بيئات تشغيل إضافية). مع تثبيت Inno Setup، ينشئ `build.ps1` برامج الإعداد أيضاً.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

البنية: [docs/02-mimari.md](docs/02-mimari.md)، والإصدار: [docs/03-yayinlama.md](docs/03-yayinlama.md) (بالتركية).

## الترجمات

ملفات اللغات موجودة في [`src/Portly/lang`](src/Portly/lang). انسخ `en.json`، وترجم القيم، واحتفظ بالعناصر النائبة `{0}`، ثم أرسل طلب سحب (pull request). المفاتيح الناقصة تعود إلى الإنجليزية. راجع [CONTRIBUTING.md](CONTRIBUTING.md).

## الخصوصية

لا يحتوي Portly على قياس عن بُعد، ولا تحليلات، ولا حسابات. يتصل بأنظمة أخرى فقط من أجل ما تبدؤه أو تفعّله:

- **فحص التحديثات** (مفعّل افتراضياً، ويمكن إيقافه من الإعدادات): ينزّل كتالوج الحزم ومعلومات الإصدارات من مستودع GitHub هذا. لا يُرسل أي شيء عنك أو عن مشاريعك.
- **تثبيت حزمة**: ينزّلها من مصدرها الرسمي (windows.php.net، dev.mysql.com، nodejs.org ...).
- **مشاركة مشروع على الإنترنت**: يشغّل `cloudflared` من Cloudflare؛ ويصل الزوار إلى موقعك عبر Cloudflare. تبقى مشاركة الشبكة المحلية داخل شبكتك.

لا يغيّر Portly إعدادات النظام إلا بعد طلب تأكيد: ملف hosts وإضافة سلطة الشهادات المحلية إلى قائمة الثقة يطلبان تأكيدك (Windows UAC).

**الإلغاء**: تُحذف النسخة المحمولة بحذف مجلدها؛ أما نسخة الإعداد فتُزال من إعدادات Windows > التطبيقات. ولإزالة سلطة الشهادات الموثوقة الخاصة بـ Portly أيضاً، افتح `certmgr.msc` > Trusted Root Certification Authorities > Certificates واحذف "Portly Local CA".

## سياسة توقيع الشيفرة

تُبنى إصدارات Windows من هذا المستودع عبر GitHub Actions. التوقيع عبر SignPath Foundation قيد الطلب؛ حتى ذلك الحين تكون الإصدارات غير موقّعة، ويُنشر مجموع SHA-256 لكل منها مع الإصدار.

أدوار الفريق:
- المؤلفون / المُودِعون: [Erdem Software](https://github.com/erdemsoftware)
- المراجعون: [Erdem Software](https://github.com/erdemsoftware)
- المُعتمِدون (توقيع الإصدارات): [Erdem Software](https://github.com/erdemsoftware)

## التبرع

Portly مجاني وسيبقى مجانياً: بلا إعلانات، ولا باقات مدفوعة، ولا تتبّع. إن وفّر لك وقتاً، يمكنك دعم تطويره.

- بطاقة: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- معرّف Binance Pay: **50018297**
- عملات رقمية (USDT على TRC-20 وBEP-20 وPolygon وSolana وEthereum): داخل التطبيق: القائمة > التبرع

---

<sub>تُنزَّل Apache وPHP وMySQL وMariaDB وPostgreSQL وMongoDB وNginx وNode.js وRedis وMemcached وMailpit وComposer وAdminer وphpMyAdmin وCloudflare والأدوات الأخرى من مصادرها الرسمية، وهي ملك لأصحابها. Portly غير مرتبط بـ Laragon.</sub>
