# My GitHub Configuration

## Account Information

| รายการ | ค่า |
|--------|-----|
| **GitHub Username** | anat-achadsiri |
| **Email** | achadsiri@gmail.com |
| **Git User Name** | achadsiri |

## Repository

| รายการ | ค่า |
|--------|-----|
| **Repository Name** | aj-design |
| **URL (SSH)** | git@github.com:anat-achadsiri/aj-design.git |
| **URL (HTTPS)** | https://github.com/anat-achadsiri/aj-design.git |

## Connection Method

โปรเจกต์นี้ใช้ **SSH** ในการเชื่อมต่อกับ GitHub

### ตรวจสอบการเชื่อมต่อ

```bash
# ทดสอบ SSH connection
ssh -T git@github.com

# ดู remote ที่ตั้งค่าไว้
git remote -v
```

### การตั้งค่า SSH Key (ถ้ายังไม่มี)

```bash
# 1. สร้าง SSH Key
ssh-keygen -t ed25519 -C "achadsiri@gmail.com"

# 2. เริ่ม SSH Agent
eval "$(ssh-agent -s)"

# 3. เพิ่ม Key ไปยัง Agent
ssh-add ~/.ssh/id_ed25519

# 4. คัดลอก Public Key
cat ~/.ssh/id_ed25519.pub
# นำไปเพิ่มที่ GitHub > Settings > SSH and GPG keys
```

### เปลี่ยนวิธีเชื่อมต่อ

```bash
# เปลี่ยนจาก HTTPS เป็น SSH
git remote set-url origin git@github.com:anat-achadsiri/aj-design.git

# เปลี่ยนจาก SSH เป็น HTTPS
git remote set-url origin https://github.com/anat-achadsiri/aj-design.git
```

## Common Git Commands

```bash
# ดูสถานะ
git status

# ดึงข้อมูลล่าสุด
git pull

# Commit และ Push
git add .
git commit -m "your message"
git push

# ดูประวัติ
git log --oneline -10
```
