# การเพิ่มโค้ดใหม่ไปยัง GitHub ส่วนตัว

## ข้อมูลบัญชี

| รายการ | ค่า |
|--------|-----|
| **GitHub Username** | anat-achadsiri |
| **Email** | achadsiri@gmail.com |

---

## ขั้นตอนการเพิ่ม Repository ใหม่

### 1. สร้าง Repository บน GitHub

1. ไปที่ https://github.com/new
2. ตั้งชื่อ Repository (เช่น `my-tools`)
3. เลือก Public หรือ Private
4. กด **Create repository**

---

### 2. เตรียม Local Repository

```bash
# เข้าไปยัง folder โปรเจกต์
cd /path/to/your/project

# สร้าง git repository
git init

# เพิ่มไฟล์ทั้งหมด
git add .

# Commit
git commit -m "Initial commit"
```

---

### 3. เชื่อมต่อกับ GitHub (ใช้ SSH)

```bash
# เพิ่ม remote (ใช้ SSH URL)
git remote add origin git@github.com:anat-achadsiri/[REPO_NAME].git

# ตรวจสอบ remote
git remote -v
```

---

### 4. เริ่ม SSH Agent (ทำทุกครั้งที่เปิด terminal ใหม่)

```bash
# เริ่ม SSH Agent
eval "$(ssh-agent -s)"

# เพิ่ม SSH Key
ssh-add ~/.ssh/id_ed25519

# ทดสอบการเชื่อมต่อ
ssh -T git@github.com
```

ถ้าสำเร็จจะเห็นข้อความ:
```
Hi anat-achadsiri! You've successfully authenticated...
```

---

### 5. Push ไปยัง GitHub

```bash
git push -u origin master
```

---

## คำสั่งที่ใช้บ่อย

```bash
# ดูสถานะ
git status

# เพิ่มไฟล์และ commit
git add .
git commit -m "your message"

# Push
git push

# Pull ข้อมูลล่าสุด
git pull
```

---

## แก้ปัญหาที่พบบ่อย

### Permission denied (publickey)

SSH Key ยังไม่ได้เพิ่มไปยัง GitHub:

1. ดู public key:
   ```bash
   cat ~/.ssh/id_ed25519.pub
   ```

2. เพิ่มที่ https://github.com/settings/keys

3. ทดสอบอีกครั้ง:
   ```bash
   ssh -T git@github.com
   ```

### Remote ใช้ HTTPS อยู่ (403 Error)

เปลี่ยนเป็น SSH:
```bash
git remote set-url origin git@github.com:anat-achadsiri/[REPO_NAME].git
```

---

## SSH Key ปัจจุบัน

```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIO3louhgdnffyU2EN8g2vJUamuQ9vDqe1FawTf9qQhCR github-key
```

Key นี้ได้เพิ่มไปยัง GitHub แล้ว
