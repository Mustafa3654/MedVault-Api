# Draft emails

Two emails, two different audiences. Send both — they unblock different things.
Review and edit before sending; the bracketed parts need your details.

---

## Email 1 — to the MedVault app team

**To:** [MedVault contact]
**Subject:** Lab Integration API — confirmation and open points

Hello [name],

Thank you for the API documentation. We have built the three endpoints against your specification and
they are running, returning the exact JSON format from your captures — including the field ordering,
the `Lab` field name, the HTML inside `ref_range`, and the entities inside `unit`.

Before we connect to your app, please confirm five points.

**1. Direction of the integration.** We understand the document as: we host the three endpoints, and
your app calls them at our address. Please confirm this is correct, and that you do not instead expect
us to write data into a database on your side.

**2. Password.** Your login capture sends only `username` and `type`, with no password field. We would
prefer to require a password issued by the hospital, so the mobile number alone does not grant access
to a patient's results. Does your app support sending a `password` field in the login request? If not,
we will match your current behaviour, but we would like this noted as a decision.

**3. Token lifetime.** Not specified in the document. We plan 12 hours, with the app re-logging in on
receiving a 401. Please confirm your app handles a 401 by re-authenticating rather than showing an
error.

**4. Radiology.** We can also expose imaging reports, as two additional endpoints in the same style
(`/api/mobile/radiology` and `/api/mobile/radiologyreport`). We kept them separate from
`/api/mobile/request` so your existing lab screen is unaffected. Let us know if this is of interest
and we will send the specification.

**5. Test window.** Once our address and certificate are ready, we would like to agree a date to test
end to end with one test patient before any real patient uses it.

Best regards,
[your name]

---

## Email 2 — to the Bekaa Hospital HIS team (English)

**To:** [HIS contact]
**Subject:** Two questions to finish the patient results integration

Hello [name],

We are building an API so that patients can see their lab and radiology reports in a mobile
application. Everything is read-only — nothing is ever written to the hospital databases.

From the WhatsApp report-delivery work we already did, we know the visit and patient structure:
`DATA1.dbo.MALADE` (`ADMI`, `ADMIP`, `NOM`, `DATEIN`) joined to `DPERSONEL.dbo.PERSONEL` on
`PERSONEL.ADMI = MALADE.ADMIP`, with the phone number in `TEL` / `TEL1`. We also use the third
character of `ADMI` to tell lab (`L`) from radiology (`X`).

Two things we could not work out from that, and they are blocking us.

**1. Where are the individual lab results stored?**

We can find a patient's lab visit, but not the test rows underneath it. For a given `ADMI`, which
table(s) hold each individual test — the test name, the result value, the unit, the reference range,
and the category (Hematology, BioChemistry, etc.)? A sample `SELECT` for one visit would be ideal.

**2. Can report PDFs be written to a folder on a server?**

Today FoxPro writes `<ADMI>*.pdf` into `C:\ExcelH` on each client PC. Our previous service reached
those PCs over the `C$` administrative share. That is not acceptable for a patient-facing service, so
we need the reports to be written — or automatically copied — to a single shared folder on a server
that the API can read.

Four smaller questions, useful but not blocking:

3. What format is stored in `PERSONEL.TEL` — local (`70123456`) or with country code (`96170123456`)?
4. Is there a status column for a visit, showing whether results are validated and releasable? We must
   never show a patient a result that has not been validated.
5. Is `DATEIN` the order time, and is the report release time stored anywhere?
6. For radiology: where are the modality (CT / MRI / X-Ray / Ultrasound), the study name, and the
   report text stored?

One more point for the hospital's decision, not a technical one: patients will need a password to log
in. There is no password column in the database today. Please advise whether the hospital wants to
issue passwords at registration, or use a one-time SMS code instead.

Thank you,
[your name]

---

## Email 2 — النسخة العربية

**الموضوع:** سؤالان لإنهاء ربط نتائج المرضى

تحية طيبة [الاسم]،

نعمل على واجهة برمجية (API) تتيح للمرضى الاطلاع على تقارير المختبر والأشعة عبر تطبيق على الهاتف.
جميع العمليات **قراءة فقط** — لا يُكتب أي شيء إطلاقًا في قواعد بيانات المستشفى.

من خلال عملنا السابق على إرسال التقارير عبر واتساب، نعرف بنية الزيارات والمرضى:
جدول `DATA1.dbo.MALADE` (الأعمدة `ADMI` و`ADMIP` و`NOM` و`DATEIN`) مرتبط بجدول
`DPERSONEL.dbo.PERSONEL` عبر `PERSONEL.ADMI = MALADE.ADMIP`، ورقم الهاتف في `TEL` أو `TEL1`.
كما نستخدم الحرف الثالث من `ADMI` للتمييز بين المختبر (`L`) والأشعة (`X`).

هناك أمران لم نتمكن من معرفتهما، وهما يوقفان العمل حاليًا:

**١. أين تُخزَّن نتائج الفحوصات التفصيلية؟**

نستطيع الوصول إلى زيارة المختبر الخاصة بالمريض، لكن ليس إلى صفوف الفحوصات ضمنها. بالنسبة لرقم زيارة
`ADMI` معيّن، ما هي الجداول التي تحتوي على كل فحص على حدة: اسم الفحص، والنتيجة، والوحدة، والمعدّل
الطبيعي، والتصنيف (أمراض الدم، الكيمياء الحيوية، إلخ)؟ ويفضَّل لو أمكن تزويدنا باستعلام `SELECT`
كمثال على زيارة واحدة.

**٢. هل يمكن حفظ ملفات التقارير (PDF) في مجلد على الخادم؟**

حاليًا يكتب برنامج FoxPro الملفات بالشكل `<ADMI>*.pdf` داخل `C:\ExcelH` على كل جهاز من أجهزة
الموظفين، وكانت خدمتنا السابقة تصل إليها عبر مشاركة `C$` الإدارية. هذا الأسلوب غير مقبول في خدمة
موجّهة للمرضى، لذلك نحتاج أن تُحفَظ التقارير — أو تُنسَخ تلقائيًا — إلى **مجلد واحد مشترك على الخادم**
يستطيع الـ API قراءته.

وأربعة أسئلة أصغر، مفيدة لكنها لا توقف العمل:

٣. ما الصيغة المخزَّنة في `PERSONEL.TEL` — محلية (`70123456`) أم مع رمز الدولة (`96170123456`)؟
٤. هل يوجد عمود لحالة الزيارة يبيّن ما إذا كانت النتائج معتمدة وجاهزة للعرض؟ من الضروري ألّا تظهر
   للمريض أي نتيجة غير معتمدة.
٥. هل `DATEIN` هو وقت طلب الفحص؟ وأين يُخزَّن وقت اعتماد التقرير؟
٦. بالنسبة للأشعة: أين يُخزَّن نوع الفحص (CT / MRI / X-Ray / Ultrasound)، واسم الفحص، ونص التقرير؟

ونقطة أخيرة تعود لقرار إدارة المستشفى وليست مسألة تقنية: سيحتاج المرضى إلى كلمة مرور لتسجيل الدخول،
ولا يوجد حاليًا عمود لكلمة المرور في قاعدة البيانات. نرجو إفادتنا إن كانت الإدارة تفضّل إصدار كلمة
مرور لكل مريض عند التسجيل، أم استخدام رمز تحقق يُرسَل عبر رسالة نصية.

مع الشكر،
[الاسم]
