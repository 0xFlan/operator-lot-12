using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace OperatorKillHouse;

public sealed class KillHouseUpdateDriver : MonoBehaviour
{
	public static Action Tick;

	public KillHouseUpdateDriver(IntPtr ptr)
		: base(ptr)
	{
	}

	public KillHouseUpdateDriver()
		: base(ClassInjector.DerivedConstructorPointer<KillHouseUpdateDriver>())
	{
		ClassInjector.DerivedConstructorBody((Il2CppObjectBase)(object)this);
	}

	private void Update()
	{
		Tick?.Invoke();
	}
}
