use std::collections::hash_map::DefaultHasher;
use std::fs;
use std::hash::{Hash, Hasher};
use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlanInfo {
    pub id: String,
    pub name: String,
    pub valid_days: i32,
    pub page_limit: i32,
    pub price_yuan: i32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LicenseStatus {
    pub auth_mode: String,
    pub udisk_drive: String,
    pub machine_code: String,
    pub serial_num: String,
    pub activated: bool,
    pub reg_code: String,
    pub plan_name: String,
    pub max_doc_count: i32,
    pub page_limit: i32,
    pub valid_days: i32,
    pub expires_on: String,
    pub use_count: i32,
    pub over_use_limit: bool,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(default)]
struct LicenseStore {
    auth_mode: String,
    udisk_drive: String,
    machine_code: String,
    serial_num: String,
    reg_code: String,
    use_count: i32,
    un_token: String,
    un1_token: String,
}

#[derive(Debug, Clone)]
struct VerifyResult {
    max_doc_count: i32,
    page_limit: i32,
    plan_index: i32,
    valid_days: i32,
    activated_on_days: i64,
}

#[derive(Debug, Error)]
pub enum LicenseError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("json error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("{0}")]
    Invalid(String),
}

pub fn list_plans() -> Vec<PlanInfo> {
    vec![
        PlanInfo { id: "trial-day".to_string(), name: "一天体验".to_string(), valid_days: 1, page_limit: 300, price_yuan: 60 },
        PlanInfo { id: "trial-week".to_string(), name: "七天体验".to_string(), valid_days: 7, page_limit: 500, price_yuan: 150 },
        PlanInfo { id: "monthly".to_string(), name: "单机月度".to_string(), valid_days: 31, page_limit: 1000, price_yuan: 299 },
        PlanInfo { id: "quarterly".to_string(), name: "单机季度".to_string(), valid_days: 93, page_limit: 2000, price_yuan: 799 },
        PlanInfo { id: "yearly".to_string(), name: "单机年度".to_string(), valid_days: 367, page_limit: 3000, price_yuan: 1999 },
        PlanInfo { id: "lifetime".to_string(), name: "单机永久".to_string(), valid_days: 0, page_limit: 20000, price_yuan: 2999 },
    ]
}

pub fn get_license_status() -> Result<LicenseStatus, LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;
    let use_count = current_use_count(&store);

    let serial_ok = verify_serial_machine_match(&store.serial_num, &store.machine_code);
    let verify = verify_reg_core(&store.serial_num, &store.reg_code);
    let clock_ok = check_clock_ok(&store.un_token);

    match verify {
        Ok(None) => Ok(LicenseStatus {
            auth_mode: store.auth_mode.clone(),
            udisk_drive: store.udisk_drive.clone(),
            machine_code: store.machine_code,
            serial_num: store.serial_num,
            activated: false,
            reg_code: store.reg_code,
            plan_name: "未激活".to_string(),
            max_doc_count: 2,
            page_limit: 3,
            valid_days: 0,
            expires_on: "-".to_string(),
            use_count,
            over_use_limit: false,
            message: if let Err(msg) = clock_ok {
                msg
            } else {
                "软件未激活，只能选择不超过3页的文档".to_string()
            },
        }),
        Ok(Some(v)) => {
            if !serial_ok {
                return Ok(LicenseStatus {
                    auth_mode: store.auth_mode.clone(),
                    udisk_drive: store.udisk_drive.clone(),
                    machine_code: store.machine_code,
                    serial_num: store.serial_num,
                    activated: false,
                    reg_code: store.reg_code,
                    plan_name: "未激活".to_string(),
                    max_doc_count: 2,
                    page_limit: 3,
                    valid_days: 0,
                    expires_on: "-".to_string(),
                    use_count,
                    over_use_limit: false,
                    message: "设备码与序列号不匹配！".to_string(),
                });
            }
            if let Err(msg) = clock_ok {
                return Ok(LicenseStatus {
                    auth_mode: store.auth_mode.clone(),
                    udisk_drive: store.udisk_drive.clone(),
                    machine_code: store.machine_code,
                    serial_num: store.serial_num,
                    activated: false,
                    reg_code: store.reg_code,
                    plan_name: "未激活".to_string(),
                    max_doc_count: 2,
                    page_limit: 3,
                    valid_days: 0,
                    expires_on: "-".to_string(),
                    use_count,
                    over_use_limit: false,
                    message: msg,
                });
            }
            let over_use = v.max_doc_count == 3 && use_count > 4;

            let expires_on = if v.valid_days <= 0 {
                "永久".to_string()
            } else {
                days_to_date(v.activated_on_days + v.valid_days as i64)
            };
            Ok(LicenseStatus {
                auth_mode: store.auth_mode.clone(),
                udisk_drive: store.udisk_drive.clone(),
                machine_code: store.machine_code,
                serial_num: store.serial_num,
                activated: true,
                reg_code: store.reg_code,
                plan_name: plan_name_from_verify(&v),
                max_doc_count: v.max_doc_count,
                page_limit: v.page_limit,
                valid_days: v.valid_days,
                expires_on,
                use_count,
                over_use_limit: over_use,
                message: if over_use {
                    "超过使用次数限制！".to_string()
                } else {
                    format!("已激活：{}", plan_name_from_verify(&v))
                },
            })
        }
        Err(msg) => Ok(LicenseStatus {
            auth_mode: store.auth_mode.clone(),
            udisk_drive: store.udisk_drive.clone(),
            machine_code: store.machine_code,
            serial_num: store.serial_num,
            activated: false,
            reg_code: store.reg_code,
            plan_name: "未激活".to_string(),
            max_doc_count: 2,
            page_limit: 3,
            valid_days: 0,
            expires_on: "-".to_string(),
            use_count,
            over_use_limit: false,
            message: msg,
        }),
    }
}

pub fn purchase_plan(plan_id: &str) -> Result<String, LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;

    let (offset, days) = plan_encode_args(plan_id)?;
    let reg = generate_reg_code_from_serial(&store.serial_num, offset, days)?;
    Ok(reg)
}

pub fn activate_license(reg_code: &str) -> Result<LicenseStatus, LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;

    let input = reg_code.trim().to_string();
    if input.is_empty() {
        return Err(LicenseError::Invalid("激活码不能为空".to_string()));
    }

    if !verify_serial_machine_match(&store.serial_num, &store.machine_code) {
        return Err(LicenseError::Invalid("设备码与序列号不匹配！".to_string()));
    }

    match verify_reg_core(&store.serial_num, &input) {
        Ok(Some(_)) => {
            store.reg_code = input;
            save_store(&store)?;
            get_license_status()
        }
        Ok(None) => Err(LicenseError::Invalid("软件未激活，只能选择不超过3页的文档".to_string())),
        Err(msg) => Err(LicenseError::Invalid(msg)),
    }
}

pub fn set_auth_mode(mode: &str, udisk_drive: Option<&str>) -> Result<LicenseStatus, LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;

    let m = mode.trim();
    if m != "device" && m != "udisk" {
        return Err(LicenseError::Invalid("认证模式仅支持 device 或 udisk".to_string()));
    }
    store.auth_mode = m.to_string();
    if m == "udisk" {
        let drive = normalize_drive_letter(udisk_drive.unwrap_or_default());
        if drive.is_empty() {
            return Err(LicenseError::Invalid("请输入正确的U盾盘符，例如 E:".to_string()));
        }
        store.udisk_drive = drive;
    }
    let new_machine = generate_machine_code(&store);
    if new_machine != store.machine_code {
        store.machine_code = new_machine;
        store.serial_num = generate_serial_num(&store.machine_code);
        store.reg_code.clear();
        store.use_count = 0;
        store.un1_token = encode_use_count_token(0);
    }
    save_store(&store)?;
    get_license_status()
}

pub fn page_limit_for_check() -> Result<i32, LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;

    if !verify_serial_machine_match(&store.serial_num, &store.machine_code) {
        return Err(LicenseError::Invalid("设备码与序列号不匹配！".to_string()));
    }

    match verify_reg_core(&store.serial_num, &store.reg_code) {
        Ok(None) => Ok(3),
        Ok(Some(v)) => Ok(v.page_limit),
        Err(msg) => Err(LicenseError::Invalid(msg)),
    }
}

pub fn validate_before_check() -> Result<(), LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;
    validate_auth_mode(&store)?;
    check_clock_ok(&store.un_token).map_err(LicenseError::Invalid)?;

    if !verify_serial_machine_match(&store.serial_num, &store.machine_code) {
        return Err(LicenseError::Invalid("设备码与序列号不匹配！".to_string()));
    }

    match verify_reg_core(&store.serial_num, &store.reg_code) {
        Ok(Some(v)) => {
            if v.max_doc_count == 3 && current_use_count(&store) > 4 {
                return Err(LicenseError::Invalid("超过使用次数限制！".to_string()));
            }
        }
        Ok(None) => {}
        Err(msg) => return Err(LicenseError::Invalid(msg)),
    }
    Ok(())
}

pub fn validate_before_choose_files(reg_code_input: Option<&str>) -> Result<(), LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;
    validate_auth_mode(&store)?;
    check_clock_ok(&store.un_token).map_err(LicenseError::Invalid)?;

    if !verify_serial_machine_match(&store.serial_num, &store.machine_code) {
        return Err(LicenseError::Invalid("设备码与序列号不匹配！".to_string()));
    }

    let effective_reg = reg_code_input
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(store.reg_code.as_str());

    match verify_reg_core(&store.serial_num, effective_reg) {
        Ok(Some(v)) => {
            if v.max_doc_count == 3 && current_use_count(&store) > 4 {
                return Err(LicenseError::Invalid("超过使用次数限制！".to_string()));
            }
            Ok(())
        }
        Ok(None) => Ok(()),
        Err(msg) => Err(LicenseError::Invalid(msg)),
    }
}

pub fn record_usage_once() -> Result<(), LicenseError> {
    let mut store = load_or_init_store()?;
    ensure_store_fields(&mut store)?;
    check_clock_ok(&store.un_token).map_err(LicenseError::Invalid)?;
    let mut count = current_use_count(&store);
    count += 1;
    store.use_count = count;
    store.un1_token = encode_use_count_token(count);
    store.un_token = encode_datetime_token();
    save_store(&store)
}

fn plan_encode_args(plan_id: &str) -> Result<(usize, i32), LicenseError> {
    match plan_id {
        "trial-day" => Ok((0, 1)),
        "trial-week" => Ok((0, 7)),
        "monthly" => Ok((0, 31)),
        "quarterly" => Ok((1, 93)),
        "yearly" => Ok((2, 367)),
        "lifetime" => Ok((3, 0)),
        _ => Err(LicenseError::Invalid("未找到套餐".to_string())),
    }
}

fn ensure_store_fields(store: &mut LicenseStore) -> Result<(), LicenseError> {
    let mut changed = false;
    if store.auth_mode.trim().is_empty() {
        store.auth_mode = "device".to_string();
        changed = true;
    }
    if store.udisk_drive.trim().is_empty() {
        store.udisk_drive = "E:".to_string();
        changed = true;
    }
    if store.machine_code.trim().is_empty() {
        store.machine_code = generate_machine_code(store);
        changed = true;
    }
    if store.serial_num.trim().is_empty() {
        store.serial_num = generate_serial_num(&store.machine_code);
        changed = true;
    }
    if store.un_token.trim().is_empty() {
        store.un_token = encode_datetime_token();
        changed = true;
    }
    if store.un1_token.trim().is_empty() {
        store.un1_token = encode_use_count_token(store.use_count.max(0));
        changed = true;
    }
    let decoded = decode_use_count_token(&store.un1_token);
    if decoded != store.use_count {
        store.use_count = decoded;
        changed = true;
    }
    if changed {
        save_store(store)?;
    }
    Ok(())
}

fn verify_serial_machine_match(serial_num: &str, machine_code: &str) -> bool {
    let value = match serial_num.trim().parse::<u64>() {
        Ok(v) => v,
        Err(_) => return false,
    };
    let mut text = format!("{:X}", value);
    if text.len() == 8 {
        text = format!("0{text}");
    }
    if text.len() != 9 {
        return false;
    }

    let chars: Vec<char> = text.chars().collect();
    let transformed: String = [chars[0], chars[2], chars[4]]
        .iter()
        .collect::<String>()
        + &chars[6..].iter().collect::<String>();

    if transformed.len() <= 4 || machine_code.len() < transformed.len() {
        return false;
    }
    let tail = &machine_code[machine_code.len() - transformed.len()..];
    transformed.eq_ignore_ascii_case(tail)
}

fn generate_serial_num(machine_code: &str) -> String {
    let mut rng = PseudoRand::new(seed_u64(machine_code));
    if machine_code.len() > 6 {
        let tail = machine_code[machine_code.len() - 6..].to_string();
        let c: Vec<char> = tail.chars().collect();
        if c.len() >= 6 {
            let raw = format!(
                "{}{}{}{}{}{}{}",
                c[0],
                rng.next_1_9(),
                c[1],
                rng.next_1_9(),
                c[2],
                rng.next_1_9(),
                c[3..].iter().collect::<String>()
            );
            if let Ok(n) = u64::from_str_radix(&raw, 16) {
                return n.to_string();
            }
        }
    }

    let a = rng.next_111_999();
    let b = rng.next_111_999();
    let c = rng.next_111_999();
    format!("{a}{b}{c}").replace('0', "1")
}

fn generate_reg_code_from_serial(serial_num: &str, offset: usize, days: i32) -> Result<String, LicenseError> {
    let mut rng = PseudoRand::new(seed_u64(serial_num));
    if serial_num.len() <= 7 {
        return Err(LicenseError::Invalid("序列号异常！".to_string()));
    }
    let chars: Vec<char> = serial_num.chars().collect();
    if offset + 4 > chars.len() {
        return Err(LicenseError::Invalid("序列号异常！".to_string()));
    }

    let core: String = chars[offset..offset + 4].iter().collect();
    let c: Vec<char> = core.chars().collect();
    let mixed = format!(
        "{}{}{}{}{}{}{}",
        c[0],
        rng.next_1_9(),
        c[1],
        rng.next_1_9(),
        c[2],
        rng.next_1_9(),
        c[3]
    );
    let mut n = mixed
        .parse::<u64>()
        .map_err(|_| LicenseError::Invalid("软件异常，请重新安装软件，或者联系售后处理！".to_string()))?;
    n = n * 499 + days as u64;

    let mut text = n.to_string();
    if text.len() < 6 {
        text = format!("{:0>6}", text);
    }

    let now = now_ymdhm();
    let d1 = (now.day / 10) as usize;
    let d2 = (now.day % 10) as usize;
    let m1 = (now.month / 10) as usize;
    let m2 = (now.month % 10) as usize;
    let y = (now.year % 100) as i32;
    let y1 = (y / 10) as usize;
    let y2 = (y % 10) as usize;

    let t: Vec<char> = text.chars().collect();
    let len = t.len();
    let suffix = format!(
        "{}{}{}{}{}{}{}{}{}{}{}{}",
        y1,
        t[len - 6],
        y2,
        t[len - 5],
        m1,
        t[len - 4],
        m2,
        t[len - 3],
        d1,
        t[len - 2],
        d2,
        t[len - 1]
    );

    Ok(format!("{}{}", &text[..len - 6], suffix))
}

fn verify_reg_core(target: &str, reg_code: &str) -> Result<Option<VerifyResult>, String> {
    if reg_code.len() <= 12 {
        return Ok(None);
    }

    let chars: Vec<char> = reg_code.chars().collect();
    let len = chars.len();

    let mut day = parse_2(chars[len - 4], chars[len - 2]).map_err(|_| "激活码D转换失败！".to_string())?;
    if day > 31 {
        day %= 10;
    }
    let mut month = parse_2(chars[len - 8], chars[len - 6]).map_err(|_| "激活码M转换失败！".to_string())?;
    if month > 20 {
        month %= 10;
    }
    let year2 = parse_2(chars[len - 12], chars[len - 10]).map_err(|_| "激活码Y转换失败！".to_string())?;
    let year = 2000 + year2;

    let active_days = civil_to_days(year, month, day).ok_or_else(|| "激活码Y转换失败！".to_string())?;

    let mut text: String = chars[..len - 12].iter().collect();
    text.push(chars[len - 11]);
    text.push(chars[len - 9]);
    text.push(chars[len - 7]);
    text.push(chars[len - 5]);
    text.push(chars[len - 3]);
    text.push(chars[len - 1]);

    let num4 = text.parse::<u64>().map_err(|_| "激活码A转换失败！".to_string())?;
    let num5 = (num4 % 499) as i32;

    let span = today_days() - active_days;
    if span > num5 as i64 && num5 > 0 {
        return Err("激活码超时！".to_string());
    }
    if span < 0 {
        return Err("激活码异常！".to_string());
    }

    let mut text2 = (num4 / 499).to_string();
    if text2.len() == 6 {
        text2 = format!("0{text2}");
    }
    if text2.len() != 7 {
        return Err("激活码与设备码不匹配！".to_string());
    }

    let t: Vec<char> = text2.chars().collect();
    let key = format!("{}{}{}{}", t[0], t[2], t[4], t[6]);
    let idx = target.find(&key);
    let Some(num6) = idx else {
        return Err("激活码与设备码不匹配！".to_string());
    };

    let sumatra_exists = sumatra_exists();
    let mut c = 2;
    let mut d = 1000;
    if num6 == 0 {
        c = 2;
        d = 1000;
        if num5 == 1 {
            d = 300;
        }
        if num5 == 7 {
            d = 500;
        }
        if num5 == 0 {
            d = 3;
            if span > num5 as i64 {
                return Err("激活码超时！".to_string());
            }
        }
        if sumatra_exists {
            c = 6;
        }
    } else if num6 == 1 {
        d = 2000;
        c = if sumatra_exists { 9 } else { 3 };
    } else if num6 == 2 {
        d = 3000;
        c = if sumatra_exists { 12 } else { 4 };
    } else {
        c = 9999;
        d = 20000;
    }

    Ok(Some(VerifyResult {
        max_doc_count: c,
        page_limit: d,
        plan_index: num6 as i32,
        valid_days: num5,
        activated_on_days: active_days,
    }))
}

fn plan_name_from_verify(v: &VerifyResult) -> String {
    match (v.plan_index, v.valid_days, v.page_limit) {
        (0, 1, 300) => "一天体验".to_string(),
        (0, 7, 500) => "七天体验".to_string(),
        (0, 31, 1000) => "单机月度".to_string(),
        (1, 93, 2000) => "单机季度".to_string(),
        (2, 367, 3000) => "单机年度".to_string(),
        (_, 0, 20000) => "单机永久".to_string(),
        _ => format!("授权版(文档数{}，页数{})", v.max_doc_count, v.page_limit),
    }
}

fn current_use_count(store: &LicenseStore) -> i32 {
    if store.un1_token.trim().is_empty() {
        return store.use_count.max(0);
    }
    decode_use_count_token(&store.un1_token).max(0)
}

fn encode_use_count_token(count: i32) -> String {
    let mut rng = PseudoRand::new(seed_u64(&format!("uc-{count}")));
    let head = rng.next_111_999() as i32;
    let value = head * 2000 + count.max(0);
    format!("{value:X}")
}

fn decode_use_count_token(token: &str) -> i32 {
    if token.trim().is_empty() {
        return 0;
    }
    i64::from_str_radix(token.trim(), 16)
        .map(|v| (v % 2000) as i32)
        .unwrap_or(0)
}

fn encode_datetime_token() -> String {
    let now = now_ymdhm();
    let raw = (now.year as i64) * 100000000
        + (now.month as i64) * 1000000
        + (now.day as i64) * 10000
        + (now.hour as i64) * 100
        + (now.minute as i64);
    let hex = format!("{raw:X}");
    if hex.len() < 3 {
        return hex;
    }
    let chars: Vec<char> = hex.chars().collect();
    let mut rng = PseudoRand::new(seed_u64(&hex));
    format!(
        "{}{}{}{}{}",
        chars[0],
        rng.next_1_9(),
        chars[1],
        rng.next_1_9(),
        chars[2..].iter().collect::<String>()
    )
}

fn check_clock_ok(token: &str) -> Result<(), String> {
    if token.trim().is_empty() {
        return Ok(());
    }
    if token.len() <= 3 {
        return Err("软件异常！".to_string());
    }
    let chars: Vec<char> = token.chars().collect();
    if chars.len() < 5 {
        return Err("软件异常！".to_string());
    }
    let rebuilt = format!("{}{}{}", chars[0], chars[2], chars[4..].iter().collect::<String>());
    let dec = i64::from_str_radix(&rebuilt, 16).map_err(|_| "软件异常！".to_string())?;
    let ds = dec.to_string();
    if ds.len() < 8 {
        return Err("软件异常！".to_string());
    }
    let year = ds[0..4].parse::<i32>().map_err(|_| "软件异常！".to_string())?;
    let month = ds[4..6].parse::<i32>().map_err(|_| "软件异常！".to_string())?;
    let day = ds[6..8].parse::<i32>().map_err(|_| "软件异常！".to_string())?;
    let saved_days = civil_to_days(year, month, day).ok_or_else(|| "软件异常！".to_string())?;
    if today_days() >= saved_days {
        Ok(())
    } else {
        Err(format!(
            "计算机时间异常！您上次使用软件的时间是：{}；现在的时间是：{}，请调整计算机时间再使用！",
            days_to_date(saved_days),
            days_to_date(today_days())
        ))
    }
}

fn sumatra_exists() -> bool {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let app_root = manifest_dir.parent().unwrap_or(&manifest_dir);
    app_root.join("SumatraPDF.exe").exists()
}

fn load_or_init_store() -> Result<LicenseStore, LicenseError> {
    let path = store_path();
    if !path.exists() {
        let mut s = LicenseStore::default();
        s.auth_mode = "device".to_string();
        s.udisk_drive = "E:".to_string();
        s.machine_code = generate_machine_code(&s);
        s.serial_num = generate_serial_num(&s.machine_code);
        s.un_token = encode_datetime_token();
        s.un1_token = encode_use_count_token(0);
        save_store(&s)?;
        return Ok(s);
    }

    let content = fs::read_to_string(&path)?;
    let store: LicenseStore = serde_json::from_str(&content)?;
    Ok(store)
}

fn save_store(store: &LicenseStore) -> Result<(), LicenseError> {
    let path = store_path();
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let text = serde_json::to_string_pretty(store)?;
    fs::write(path, text)?;
    Ok(())
}

fn store_path() -> PathBuf {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let app_root = manifest_dir.parent().unwrap_or(&manifest_dir);
    app_root.join("data").join("license.json")
}

fn generate_machine_code(store: &LicenseStore) -> String {
    if store.auth_mode == "udisk" {
        let drive = normalize_drive_letter(&store.udisk_drive);
        if !drive.is_empty() {
            let seed = format!("UDISK|{}|AICHECKBID", drive.to_uppercase());
            let mut hasher = DefaultHasher::new();
            seed.hash(&mut hasher);
            return format!("{:016X}", hasher.finish());
        }
    }
    let computer = std::env::var("COMPUTERNAME").unwrap_or_else(|_| "UNKNOWNPC".to_string());
    let cpu = std::env::var("PROCESSOR_IDENTIFIER").unwrap_or_else(|_| "UNKNOWNCPU".to_string());
    let seed = format!("{computer}|{cpu}|AICHECKBID");
    let mut hasher = DefaultHasher::new();
    seed.hash(&mut hasher);
    format!("{:016X}", hasher.finish())
}

fn validate_auth_mode(store: &LicenseStore) -> Result<(), LicenseError> {
    if store.auth_mode != "udisk" {
        return Ok(());
    }
    let drive = normalize_drive_letter(&store.udisk_drive);
    if drive.is_empty() {
        return Err(LicenseError::Invalid(
            "U盾未插入，如果U盾已插入，请在认证模式的输入框中输入正确的U盾的盘符！".to_string(),
        ));
    }
    let root = format!("{drive}\\");
    if !PathBuf::from(&root).exists() {
        return Err(LicenseError::Invalid(
            "U盾未插入，如果U盾已插入，请在认证模式的输入框中输入正确的U盾的盘符！".to_string(),
        ));
    }
    Ok(())
}

fn normalize_drive_letter(input: &str) -> String {
    let trimmed = input.trim().trim_end_matches('\\').trim_end_matches('/');
    if trimmed.is_empty() {
        return String::new();
    }
    let mut chars = trimmed.chars();
    let Some(first) = chars.next() else {
        return String::new();
    };
    if !first.is_ascii_alphabetic() {
        return String::new();
    }
    format!("{}:", first.to_ascii_uppercase())
}

fn parse_2(a: char, b: char) -> Result<i32, ()> {
    let s = format!("{a}{b}");
    s.parse::<i32>().map_err(|_| ())
}

fn seed_u64(input: &str) -> u64 {
    let mut hasher = DefaultHasher::new();
    input.hash(&mut hasher);
    hasher.finish()
}

struct PseudoRand {
    state: u64,
}

impl PseudoRand {
    fn new(seed: u64) -> Self {
        Self { state: seed ^ 0x9E37_79B9_7F4A_7C15 }
    }

    fn next(&mut self) -> u64 {
        self.state = self.state.wrapping_mul(6364136223846793005).wrapping_add(1);
        self.state
    }

    fn next_1_9(&mut self) -> u8 {
        ((self.next() % 9) + 1) as u8
    }

    fn next_111_999(&mut self) -> u16 {
        ((self.next() % 889) + 111) as u16
    }
}

#[derive(Clone, Copy)]
struct Ymd {
    year: i32,
    month: i32,
    day: i32,
    hour: i32,
    minute: i32,
}

fn now_ymdhm() -> Ymd {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64;
    let days = secs / 86_400;
    let sec_of_day = secs % 86_400;
    let (year, month, day) = days_to_civil(days);
    Ymd {
        year,
        month,
        day,
        hour: (sec_of_day / 3600) as i32,
        minute: ((sec_of_day % 3600) / 60) as i32,
    }
}

fn today_days() -> i64 {
    (std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64)
        / 86_400
}

fn days_to_date(days: i64) -> String {
    let (y, m, d) = days_to_civil(days);
    format!("{y:04}-{m:02}-{d:02}")
}

fn civil_to_days(year: i32, month: i32, day: i32) -> Option<i64> {
    if month < 1 || month > 12 || day < 1 || day > 31 {
        return None;
    }
    let y = year - if month <= 2 { 1 } else { 0 };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let m = month + if month > 2 { -3 } else { 9 };
    let doy = (153 * m + 2) / 5 + day - 1;
    if doy < 0 || doy > 365 {
        return None;
    }
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    Some((era as i64) * 146097 + (doe as i64) - 719468)
}

fn days_to_civil(days_since_epoch: i64) -> (i32, i32, i32) {
    let z = days_since_epoch + 719468;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = mp + if mp < 10 { 3 } else { -9 };
    let year = y + if m <= 2 { 1 } else { 0 };
    (year as i32, m as i32, d as i32)
}
