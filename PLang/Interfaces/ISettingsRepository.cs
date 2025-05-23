﻿using Newtonsoft.Json;
using Org.BouncyCastle.Bcpg;
using PLang.Errors;
using PLang.Models;

namespace PLang.Interfaces
{

    public interface ISettingsRepository
    {
		bool IsDefaultSystemDbPath { get; }
		string DataSource { get; }
		public IEnumerable<Setting> GetSettings();
        public void Set(Setting setting);
        void Remove(Setting setting);
		void SetSharedDataSource(string? appId = null);
		Setting? Get(string? fullName, string? type, string? key);
		string SerializeSettings();
		IError? SetSystemDbPath(string path);
		void ResetSystemDbPath();
	}
}
