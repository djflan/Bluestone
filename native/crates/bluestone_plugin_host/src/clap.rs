use std::path::Path;
use std::sync::Arc;

use crate::common::*;

pub struct ClapModule;

impl ClapModule {
    pub fn open(_path: &Path) -> Result<Arc<ClapModule>, String> {
        Err("CLAP is not implemented yet.".into())
    }

    pub fn describe(&self) -> Vec<PluginInfo> {
        Vec::new()
    }
}

pub struct ClapInstance;

impl ClapInstance {
    pub fn create(_module: Arc<ClapModule>, _id: &str) -> Result<ClapInstance, String> {
        Err("CLAP is not implemented yet.".into())
    }
}

impl Instance for ClapInstance {
    fn parameters(&self) -> Vec<ParamInfo> {
        Vec::new()
    }
    fn get_parameter(&self, _id: u32) -> Option<f64> {
        None
    }
    fn set_parameter(&self, _id: u32, _value: f64) -> bool {
        false
    }
    fn activate(
        &self,
        _sample_rate: f64,
        _max_frames: u32,
        _input_channels: u32,
        _output_channels: u32,
    ) -> Result<(), String> {
        Err(String::new())
    }
    fn process(&self, _block: &mut BphProcess) -> bool {
        false
    }
    fn latency(&self) -> u32 {
        0
    }
    fn save_state(&self) -> Result<Vec<u8>, String> {
        Err(String::new())
    }
    fn load_state(&self, _data: &[u8]) -> Result<(), String> {
        Err(String::new())
    }
    fn idle(&self, _notifications: &mut Vec<BphNotification>) {}
    fn has_editor(&self) -> bool {
        false
    }
    fn open_editor(&self, _title: &str) -> Result<(), String> {
        Err(String::new())
    }
    fn close_editor(&self) {}
    fn is_editor_open(&self) -> bool {
        false
    }
}
